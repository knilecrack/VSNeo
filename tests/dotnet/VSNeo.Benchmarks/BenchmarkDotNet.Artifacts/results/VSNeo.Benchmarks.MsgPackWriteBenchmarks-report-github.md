```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26220.9568)
Unknown processor
.NET SDK 10.0.401
  [Host]   : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL
  ShortRun : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                  | Mean      | Error    | StdDev   | Gen0   | Allocated |
|------------------------ |----------:|---------:|---------:|-------:|----------:|
| WriteInputFrame         | 134.67 ns | 18.08 ns | 0.991 ns | 0.0627 |     632 B |
| WriteInputFramePooled   |  97.60 ns | 13.44 ns | 0.737 ns | 0.0063 |      64 B |
| WriteSetTextFrame       | 205.87 ns | 18.15 ns | 0.995 ns | 0.0658 |     664 B |
| WriteSetTextFramePooled | 175.04 ns | 59.22 ns | 3.246 ns | 0.0095 |      96 B |
