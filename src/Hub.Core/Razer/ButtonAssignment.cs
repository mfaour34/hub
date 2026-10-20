namespace Hub.Core.Razer;

/// <summary>Function ids for 02,0C button assignments (docs/protocols.md).</summary>
public enum ButtonFunction : byte
{
    Off = 0,
    MouseButton = 1,
    Key = 2,
    Dpi = 6,
    Profile = 7,
    MediaKey = 10,
    DoubleClick = 11,
    TurboKey = 13,
    TurboButton = 14,
}

/// <summary>Firmware button ids on the Viper V3 Pro.</summary>
public static class ViperButtons
{
    public const byte Left = 1, Right = 2, Middle = 3, Back = 4, Forward = 5, WheelUp = 9, WheelDown = 10, BottomDpi = 96;

    public static readonly IReadOnlyDictionary<byte, string> Names = new Dictionary<byte, string>
    {
        [Left] = "Left click", [Right] = "Right click", [Middle] = "Wheel click", [Back] = "Back (side)",
        [Forward] = "Forward (side)", [WheelUp] = "Wheel up", [WheelDown] = "Wheel down", [BottomDpi] = "Bottom DPI button",
    };
}

public sealed record ButtonAssignment(byte ButtonId, ButtonFunction Function, byte[] Data)
{
    public static ButtonAssignment Default(byte buttonId) =>
        buttonId == ViperButtons.BottomDpi
            ? new(buttonId, ButtonFunction.Dpi, [6])
            : new(buttonId, ButtonFunction.MouseButton, [buttonId]);

    public static ButtonAssignment MouseButton(byte buttonId, byte code) => new(buttonId, ButtonFunction.MouseButton, [code]);

    /// <summary>Keyboard key: [modifier mask, HID usage]. Used for host-side macros via spare keys like F13..F24.</summary>
    public static ButtonAssignment Key(byte buttonId, byte hidUsage, byte modifiers = 0) => new(buttonId, ButtonFunction.Key, [modifiers, hidUsage]);

    public static ButtonAssignment DpiCycleUp(byte buttonId) => new(buttonId, ButtonFunction.Dpi, [6]);

    public static ButtonAssignment Disabled(byte buttonId) => new(buttonId, ButtonFunction.Off, []);

    public string Describe() => Function switch
    {
        ButtonFunction.MouseButton when Data.Length > 0 => Data[0] switch
        {
            1 => "Left click", 2 => "Right click", 3 => "Middle click", 4 => "Back", 5 => "Forward",
            9 => "Wheel up", 10 => "Wheel down", var c => $"Mouse button {c}",
        },
        ButtonFunction.Key when Data.Length > 1 => $"Key 0x{Data[1]:X2}" + (Data[0] != 0 ? $" (mods 0x{Data[0]:X2})" : ""),
        ButtonFunction.Dpi when Data.Length > 0 => Data[0] switch
        {
            1 => "DPI up", 2 => "DPI down", 6 => "DPI cycle up", 7 => "DPI cycle down", _ => "DPI",
        },
        ButtonFunction.Off => "Disabled",
        _ => $"{Function} [{Convert.ToHexString(Data)}]",
    };
}
