namespace Oathbound.Plugin.Commands;

/// Local chat lines in the plugin's own name. Printed on this client only; nothing here is ever sent.
public static class OathboundChat
{
    private const string Tag = "Oathbound";

    public static void Print(string text) => Plugin.ChatGui.Print(text, Tag);

    public static void PrintError(string text) => Plugin.ChatGui.PrintError(text, Tag);
}
