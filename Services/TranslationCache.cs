using System.Collections.Concurrent;

namespace ScreenTranslator.Services;

/// <summary>
/// 译文缓存（内存 LRU）。
///
/// 屏幕上的重复内容非常多（导航栏、按钮、固定标题、以及滚动回看的旧内容），
/// 缓存能显著减少 API 调用次数与延迟。键为标准化后的原文，值为译文。
/// </summary>
public class TranslationCache
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru = new();

    private sealed class Entry
    {
        public string Key = string.Empty;
        public string Value = string.Empty;
    }

    /// <summary>命中次数</summary>
    public long Hits { get; private set; }

    /// <summary>未命中次数</summary>
    public long Misses { get; private set; }

    public int Count { get { lock (_lock) return _map.Count; } }

    public TranslationCache(int capacity = 2000)
    {
        _capacity = Math.Max(64, capacity);
        _map = new Dictionary<string, LinkedListNode<Entry>>(_capacity);
    }

    /// <summary>尝试取缓存</summary>
    public bool TryGet(string key, out string value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                Hits++;
                value = node.Value.Value;
                return true;
            }
        }
        Misses++;
        value = string.Empty;
        return false;
    }

    /// <summary>写入缓存</summary>
    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value.Value = value;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry { Key = key, Value = value });
            _lru.AddFirst(node);
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var last = _lru.Last;
                if (last == null) break;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            Hits = 0;
            Misses = 0;
        }
    }
}
