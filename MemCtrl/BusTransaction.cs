using System;
using System.Collections.Generic;
using System.Text;

using System.IO;

namespace MemMap
{
    public struct BusTransaction
    {
        public MemAddr addr;
        public long ts;

        public BusTransaction(MemAddr addr, long ts)
        {
            this.addr = addr;
            this.ts = ts;
        }
    }
}