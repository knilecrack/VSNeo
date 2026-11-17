using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VSNeo_Extension.Nvim;

/// <summary>An nvim Buffer (0), Window (1) or Tabpage (2) handle.</summary>
internal readonly struct NvimHandle
{
    public readonly sbyte Kind;
    public readonly long Id;

    public NvimHandle(sbyte kind, long id)
    {
        Kind = kind;
        Id = id;
    }

    public override string ToString() => "handle(" + Kind + ":" + Id + ")";
}
