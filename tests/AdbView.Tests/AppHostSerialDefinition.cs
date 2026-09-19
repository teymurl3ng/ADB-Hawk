namespace AdbView.Tests;

using Xunit;

/// <summary>
/// AppHost uses a process-wide single-instance mutex. Parallel CompositionTests
/// contend for it and the loser path calls Environment.Exit, which wedges MTP.
/// </summary>
[CollectionDefinition("AppHostSerial", DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires public collection definitions
#pragma warning disable CA1812 // Instantiated by xUnit reflection
public sealed class AppHostSerialDefinition;
#pragma warning restore CA1812
#pragma warning restore CA1515
