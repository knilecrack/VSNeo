```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26220.9568)
Unknown processor
.NET SDK 10.0.401
  [Host]   : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL
  ShortRun : .NET 8.0.31 (8.0.3126.42015), X64 RyuJIT AVX-512F+CD+BW+DQ+VL

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method          | Mean      | Error     | StdDev   | Allocated |
|---------------- |----------:|----------:|---------:|----------:|
| ByteToCharAscii |  22.83 ns |  1.457 ns | 0.080 ns |         - |
| CharToByteAscii |  23.35 ns |  0.644 ns | 0.035 ns |         - |
| ByteToCharMixed | 210.42 ns | 17.241 ns | 0.945 ns |         - |
| CharToByteMixed | 170.60 ns | 16.955 ns | 0.929 ns |         - |
