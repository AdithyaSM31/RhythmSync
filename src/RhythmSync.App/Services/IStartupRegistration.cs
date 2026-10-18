namespace RhythmSync.App.Services;

/// <summary>Whether RhythmSync launches when the user signs in to Windows.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    /// <summary>Registers RhythmSync to start at sign-in. May throw when the registration cannot be written.</summary>
    void Enable();

    /// <summary>Removes the registration. May throw when it cannot be changed.</summary>
    void Disable();
}
