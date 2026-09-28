```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26220.9568)
Unknown processor
.NET SDK 10.0.401
  [Host]   : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL
  ShortRun : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method          | Mean        | Error       | StdDev    | Gen0    | Gen1   | Allocated |
|---------------- |------------:|------------:|----------:|--------:|-------:|----------:|
| ReadStateFrame  |    170.2 ns |     1.48 ns |   0.08 ns |  0.0420 |      - |     424 B |
| ReadRedrawFrame |  7,136.0 ns |   153.78 ns |   8.43 ns |  0.0610 |      - |     640 B |
| ReadLinesFrame  | 22,708.1 ns | 5,037.02 ns | 276.10 ns | 11.4746 | 3.5706 |  115584 B |
