namespace ALyricEase.Infrastructure;

/// <summary>
/// 只保存逻辑下标的洗牌袋。一轮内每个候选最多取出一次；歌曲元数据仍由调用方按命中下标解析。
/// </summary>
internal sealed class ShuffleIndexBag
{
    private readonly List<int> _remaining = new();

    public int Count => _remaining.Count;

    public void Clear() => _remaining.Clear();

    public void Refill(int count, int currentIndex, IReadOnlySet<int> excluded, Random random)
    {
        _remaining.Clear();
        for (var index = 0; index < count; index++)
            if (index != currentIndex && !excluded.Contains(index))
                _remaining.Add(index);

        // Fisher-Yates；从尾部 Take，整个下标集合只分配一次。
        for (var i = _remaining.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (_remaining[i], _remaining[j]) = (_remaining[j], _remaining[i]);
        }
    }

    public bool TryTake(out int index)
    {
        if (_remaining.Count == 0)
        {
            index = -1;
            return false;
        }

        var last = _remaining.Count - 1;
        index = _remaining[last];
        _remaining.RemoveAt(last);
        return true;
    }

    /// <summary>瞬时失败不消耗候选；放到袋首，避免下一次立刻再次命中。</summary>
    public void PutBack(int index)
    {
        if (index >= 0 && !_remaining.Contains(index))
            _remaining.Insert(0, index);
    }

    public void Exclude(int index) => _remaining.RemoveAll(candidate => candidate == index);

    /// <summary>普通队列删除元素后保持剩余下标对齐。</summary>
    public void RemoveAndShift(int removedIndex)
    {
        for (var i = _remaining.Count - 1; i >= 0; i--)
        {
            if (_remaining[i] == removedIndex)
                _remaining.RemoveAt(i);
            else if (_remaining[i] > removedIndex)
                _remaining[i]--;
        }
    }

    /// <summary>普通队列插入“下一首播放”后保持剩余下标对齐；新插入项不加入本轮。</summary>
    public void InsertAndShift(int insertedIndex)
    {
        for (var i = 0; i < _remaining.Count; i++)
            if (_remaining[i] >= insertedIndex)
                _remaining[i]++;
    }
}
