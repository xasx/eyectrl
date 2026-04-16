namespace EyeCtrl.Core.Enums;

public enum GazeProviderType
{
    Auto = 0,
    [Obsolete("The 32-bit Interaction Library is no longer supported. Use StreamEngine instead.")]
    InteractionLib = 1,
    StreamEngine = 2,
    Mouse = 3
}
