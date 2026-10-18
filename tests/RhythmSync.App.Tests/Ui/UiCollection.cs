namespace RhythmSync.App.Tests.Ui;

/// <summary>UI smoke tests share one STA dispatcher and one WPF Application, so they run one at a time.</summary>
[CollectionDefinition("Ui", DisableParallelization = true)]
public sealed class UiCollection
{
}
