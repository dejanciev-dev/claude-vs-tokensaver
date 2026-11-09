using System;

namespace ClaudeVS
{
    /// <summary>Must match the GuidSymbol / IDSymbol values in ClaudeVSPackage.vsct.</summary>
    internal static class PackageIds
    {
        public const string PackageGuidString = "a5234be0-66c2-40a1-9e7f-ca291c8100e0";
        public const string CmdSetGuidString = "9092e8e3-816e-4fb6-8ee2-440557e650c9";
        public const string ToolWindowGuidString = "ea1b59f4-7311-4d43-bb62-9390d14aa045";

        public static readonly Guid CmdSet = new Guid(CmdSetGuidString);

        public const int OpenWindow = 0x0100;
        public const int SendSelection = 0x0101;
        public const int GenerateClaudeMd = 0x0102;
        public const int InstallTokenSaver = 0x0103;
        public const int AttachCurrentFile = 0x0104;
        public const int AttachErrorList = 0x0105;
        public const int ShowSavingsReport = 0x0106;
        public const int NewChatWindow = 0x0107;
        public const int FocusLastMessage = 0x0108;
        public const int ExportUsageCsv = 0x0109;
    }
}
