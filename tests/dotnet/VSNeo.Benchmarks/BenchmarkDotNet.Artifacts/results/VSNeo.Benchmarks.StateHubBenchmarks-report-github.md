```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26220.9568)
Unknown processor
.NET SDK 10.0.401
  [Host]   : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL
  ShortRun : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method        | Mean     | Error    | StdDev   | Gen0   | Allocated |
|-------------- |---------:|---------:|---------:|-------:|----------:|
| StatePush     | 26.23 ns | 0.400 ns | 0.022 ns |      - |         - |
| RedrawShowCmd | 72.29 ns | 4.286 ns | 0.235 ns | 0.0135 |     136 B |
