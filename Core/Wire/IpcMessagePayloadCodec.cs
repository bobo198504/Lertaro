namespace Lertaro.Core.Wire;

// Split out of PipeRequestBinarySerializer purely to keep that file under the repo's per-file line limit:
// this is the IpcMessage payload codec (the per-kind field encoding), while the serializer keeps the frame
// header, magic/version checks and the stream plumbing. No state of its own beyond the shared JSON options.
internal static class IpcMessagePayloadCodec
{
    internal static void WritePayload(BinaryWriter writer, IpcMessage msg)
    {
        writer.Write((byte)msg.Id);
        switch (msg.Id)
        {
            case IpcMessageId.Stop:
            case IpcMessageId.ReloadSettings:
            case IpcMessageId.RequestOpenedFolders:
            case IpcMessageId.Activate:
            case IpcMessageId.ExplorerDeactivated:
            case IpcMessageId.ActiveWindowMoved:
            case IpcMessageId.KeyBackspace:
            case IpcMessageId.KeyEscape:
            case IpcMessageId.KeyEnter:
            case IpcMessageId.KeyUp:
            case IpcMessageId.QuickPanelHotkey:
            case IpcMessageId.QuickNavigationHotkey:
            case IpcMessageId.KeyDown:
            case IpcMessageId.KeyLeft:
            case IpcMessageId.KeyRight:
                break;
            case IpcMessageId.SetAppProcessId:
            case IpcMessageId.KillProcess:
                writer.Write(msg.ProcessId);
                break;

            case IpcMessageId.SetQuickSearchVisible:
            case IpcMessageId.SetInlineSearchVisible:
            case IpcMessageId.SetInlineWindowOnScreen:
            case IpcMessageId.SetHotkeysDisabled:
                writer.Write(msg.BoolVal);
                break;

            case IpcMessageId.NavigateDialog:
                writer.Write(msg.Hwnd);
                writer.Write(msg.StringVal1 ?? string.Empty);
                break;

            case IpcMessageId.RestoreDialogFocus:
                writer.Write(msg.Hwnd);
                break;

            case IpcMessageId.ForceForeground:
                writer.Write(msg.Hwnd);
                writer.Write(msg.BoolVal);
                break;

            case IpcMessageId.KeyChar:
                writer.Write(msg.CharVal);
                break;

            case IpcMessageId.KeyCtrlNumber:
                writer.Write(msg.IntVal);
                break;

            case IpcMessageId.MouseClick:
            case IpcMessageId.MouseDoubleClick:
            case IpcMessageId.MouseMiddleClick:
                writer.Write(msg.MouseX);
                writer.Write(msg.MouseY);
                break;

            case IpcMessageId.ExplorerActivated:
                writer.Write(msg.Hwnd);
                writer.Write(msg.StringVal1 ?? string.Empty);
                writer.Write(msg.StringVal2 ?? string.Empty);
                writer.Write(msg.IsDesktop);
                break;

            case IpcMessageId.PathCaptured:
            case IpcMessageId.Error:
                writer.Write(msg.StringVal1 ?? string.Empty);
                if (msg.Id == IpcMessageId.PathCaptured)
                {
                    writer.Write(msg.IsDesktop);
                    writer.Write(msg.IsDialog);
                }
                break;

            case IpcMessageId.ExecuteInlineItem:
                writer.Write(msg.Hwnd);
                writer.Write(msg.StringVal1 ?? string.Empty);
                writer.Write(msg.StringVal2 ?? string.Empty);
                writer.Write(msg.IntVal);
                break;

            case IpcMessageId.InlineSelectionChanged:
                writer.Write(msg.Hwnd);
                writer.Write(msg.StringVal1 ?? string.Empty);
                break;

            case IpcMessageId.InlineSearchFinished:
                writer.Write(msg.Hwnd);
                writer.Write(msg.BoolVal);
                break;

            case IpcMessageId.ExecuteInlineItemResponse:
                writer.Write(msg.IntVal);
                writer.Write(msg.BoolVal);
                break;

            case IpcMessageId.OpenedFoldersCaptured:
                var paths = msg.StringList ?? Array.Empty<string>();
                writer.Write(paths.Count);
                foreach (var path in paths)
                    writer.Write(path);
                break;

            case IpcMessageId.RunTool:
                writer.Write(msg.StringVal1 ?? string.Empty);
                writer.Write(msg.StringVal2 ?? string.Empty);
                break;

            case IpcMessageId.ToolResult:
                writer.Write(msg.BoolVal);
                writer.Write(msg.IntVal);
                writer.Write(msg.StringVal1 ?? string.Empty);
                break;
        }
    }

    internal static IpcMessage ReadPayload(BinaryReader reader)
    {
        var msg = new IpcMessage { Id = (IpcMessageId)reader.ReadByte() };
        switch (msg.Id)
        {
            case IpcMessageId.Stop:
            case IpcMessageId.ReloadSettings:
            case IpcMessageId.RequestOpenedFolders:
            case IpcMessageId.Activate:
            case IpcMessageId.ExplorerDeactivated:
            case IpcMessageId.ActiveWindowMoved:
            case IpcMessageId.KeyBackspace:
            case IpcMessageId.KeyEscape:
            case IpcMessageId.KeyEnter:
            case IpcMessageId.KeyUp:
            case IpcMessageId.QuickPanelHotkey:
            case IpcMessageId.QuickNavigationHotkey:
            case IpcMessageId.KeyDown:
            case IpcMessageId.KeyLeft:
            case IpcMessageId.KeyRight:
                break;

            case IpcMessageId.SetAppProcessId:
            case IpcMessageId.KillProcess:
                msg.ProcessId = reader.ReadUInt32();
                break;

            case IpcMessageId.SetQuickSearchVisible:
            case IpcMessageId.SetInlineSearchVisible:
            case IpcMessageId.SetInlineWindowOnScreen:
            case IpcMessageId.SetHotkeysDisabled:
                msg.BoolVal = reader.ReadBoolean();
                break;

            case IpcMessageId.NavigateDialog:
                msg.Hwnd = reader.ReadInt64();
                msg.StringVal1 = reader.ReadString();
                break;

            case IpcMessageId.RestoreDialogFocus:
                msg.Hwnd = reader.ReadInt64();
                break;

            case IpcMessageId.ForceForeground:
                msg.Hwnd = reader.ReadInt64();
                msg.BoolVal = reader.ReadBoolean();
                break;

            case IpcMessageId.KeyChar:
                msg.CharVal = reader.ReadChar();
                break;

            case IpcMessageId.KeyCtrlNumber:
                msg.IntVal = reader.ReadInt32();
                break;

            case IpcMessageId.MouseClick:
            case IpcMessageId.MouseDoubleClick:
            case IpcMessageId.MouseMiddleClick:
                msg.MouseX = reader.ReadInt32();
                msg.MouseY = reader.ReadInt32();
                break;

            case IpcMessageId.ExplorerActivated:
                msg.Hwnd = reader.ReadInt64();
                msg.StringVal1 = reader.ReadString();
                msg.StringVal2 = reader.ReadString();
                msg.IsDesktop = reader.ReadBoolean();
                break;

            case IpcMessageId.PathCaptured:
                msg.StringVal1 = reader.ReadString();
                msg.IsDesktop = reader.ReadBoolean();
                msg.IsDialog = reader.ReadBoolean();
                break;

            case IpcMessageId.Error:
                msg.StringVal1 = reader.ReadString();
                break;

            case IpcMessageId.ExecuteInlineItem:
                msg.Hwnd = reader.ReadInt64();
                msg.StringVal1 = reader.ReadString();
                msg.StringVal2 = reader.ReadString();
                msg.IntVal = reader.ReadInt32();
                break;

            case IpcMessageId.InlineSelectionChanged:
                msg.Hwnd = reader.ReadInt64();
                msg.StringVal1 = reader.ReadString();
                break;

            case IpcMessageId.InlineSearchFinished:
                msg.Hwnd = reader.ReadInt64();
                msg.BoolVal = reader.ReadBoolean();
                break;

            case IpcMessageId.ExecuteInlineItemResponse:
                msg.IntVal = reader.ReadInt32();
                msg.BoolVal = reader.ReadBoolean();
                break;

            case IpcMessageId.OpenedFoldersCaptured:
                var count = reader.ReadInt32();
                if (count < 0 || count > 10_000)
                    throw new InvalidDataException("Opened-folder snapshot exceeds the IPC limit.");
                var paths = new string[count];
                for (var index = 0; index < count; index++)
                    paths[index] = reader.ReadString();
                msg.StringList = paths;
                break;

            case IpcMessageId.RunTool:
                msg.StringVal1 = reader.ReadString();
                msg.StringVal2 = reader.ReadString();
                break;

            case IpcMessageId.ToolResult:
                msg.BoolVal = reader.ReadBoolean();
                msg.IntVal = reader.ReadInt32();
                msg.StringVal1 = reader.ReadString();
                break;
        }

        return msg;
    }

}
