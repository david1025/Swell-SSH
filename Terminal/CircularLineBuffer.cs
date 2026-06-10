namespace SwellSSH.Terminal
{
    /// <summary>
    /// O(1) 头尾操作的环形行缓冲区，用于替代 TerminalBuffer.Lines 的 List&lt;TerminalRow&gt;。
    /// 按索引随机访问同样 O(1)。容量固定，Resize 时重建。
    /// </summary>
    public sealed class CircularLineBuffer
    {
        private TerminalRow[] _buf;
        private int _head; // 逻辑 index 0 对应的物理位置
        private int _count;

        public CircularLineBuffer(int capacity)
        {
            _buf   = new TerminalRow[capacity > 0 ? capacity : 1];
            _head  = 0;
            _count = 0;
        }

        public int Count    => _count;
        public int Capacity => _buf.Length;

        public TerminalRow this[int index]
        {
            get => _buf[PhysIdx(index)];
            set => _buf[PhysIdx(index)] = value;
        }

        /// <summary>末尾追加（等同于原 List.Add）。</summary>
        public void AddLast(TerminalRow row)
        {
            EnsureCapacity(_count + 1);
            _buf[PhysIdx(_count)] = row;
            _count++;
        }

        /// <summary>头部插入 O(1)（等同于原 List.Insert(0, ...)）。</summary>
        public void AddFirst(TerminalRow row)
        {
            EnsureCapacity(_count + 1);
            _head = (_head - 1 + _buf.Length) % _buf.Length;
            _buf[_head] = row;
            _count++;
        }

        /// <summary>移除头部并返回 O(1)（等同于原 List.RemoveAt(0) + 取值）。</summary>
        public TerminalRow RemoveFirst()
        {
            var row = _buf[_head];
            _head = (_head + 1) % _buf.Length;
            _count--;
            return row;
        }

        /// <summary>移除尾部 O(1)（等同于原 List.RemoveAt(Count-1)）。</summary>
        public TerminalRow RemoveLast()
        {
            _count--;
            return _buf[PhysIdx(_count)];
        }

        /// <summary>调整行数（Resize 时调用），用新行填充多余位置。</summary>
        public void Resize(int newCount, int cols)
        {
            // 重建连续数组，方便后续 resize 的列宽调整
            var newBuf = new TerminalRow[newCount];
            int copy = System.Math.Min(_count, newCount);
            for (int i = 0; i < copy; i++)
                newBuf[i] = _buf[PhysIdx(i)];
            for (int i = copy; i < newCount; i++)
                newBuf[i] = new TerminalRow(cols);
            _buf   = newBuf;
            _head  = 0;
            _count = newCount;
        }

        private int PhysIdx(int logical) => (_head + logical) % _buf.Length;

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buf.Length) return;
            int newCap = System.Math.Max(_buf.Length * 2, needed);
            var newBuf = new TerminalRow[newCap];
            for (int i = 0; i < _count; i++)
                newBuf[i] = _buf[PhysIdx(i)];
            _buf  = newBuf;
            _head = 0;
        }
    }
}
