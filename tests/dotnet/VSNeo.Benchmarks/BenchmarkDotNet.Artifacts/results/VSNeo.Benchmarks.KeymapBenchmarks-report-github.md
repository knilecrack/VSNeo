```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26220.9568)
Unknown processor
.NET SDK 10.0.401
  [Host]   : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL
  ShortRun : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method      | Mean         | Error       | StdDev     | Gen0   | Gen1   | Allocated |
|------------ |-------------:|------------:|-----------:|-------:|-------:|----------:|
| Children    | 1,398.512 ns | 406.9914 ns | 22.3086 ns | 0.4177 | 0.0038 |    4208 B |
| HasChildren |     5.099 ns |   0.9013 ns |  0.0494 ns |      - |      - |         - |
| Split       |    98.432 ns |  24.3689 ns |  1.3357 ns | 0.0293 |      - |     296 B |
