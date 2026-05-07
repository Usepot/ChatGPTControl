using System.ComponentModel;
using System.Runtime.InteropServices;
using PortableCodex.Native.Models;

namespace PortableCodex.Native.Services;

public sealed partial class FileToolService
{
    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;

    private async Task<ToolResponse> HandleClickDesktopAsync(
        ToolRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var x = request.X ?? throw new InvalidOperationException("x is required for click_desktop");
        var y = request.Y ?? throw new InvalidOperationException("y is required for click_desktop");
        var button = NormalizeMouseButton(request.Button);
        var clicks = request.Clicks.GetValueOrDefault(1);
        if (clicks < 1 || clicks > 10)
        {
            throw new InvalidOperationException("clicks must be between 1 and 10 for click_desktop");
        }

        var approved = await EnsureWriteApprovalAsync(
            request,
            $"Click desktop at ({x}, {y}) with {button} button ({clicks} click{(clicks == 1 ? string.Empty : "s")})",
            context);
        if (!approved)
        {
            return Denied(request.RequestId, "User denied click_desktop request");
        }

        await Task.Run(() => SendMouseClick(x, y, button, clicks, cancellationToken), cancellationToken);

        return Ok(
            request.RequestId,
            new
            {
                x,
                y,
                button,
                clicks,
                clicked = true,
            });
    }

    private static string NormalizeMouseButton(string? button)
    {
        if (string.IsNullOrWhiteSpace(button))
        {
            return "left";
        }

        return button.ToLowerInvariant() switch
        {
            "left" => "left",
            "right" => "right",
            "middle" => "middle",
            _ => throw new InvalidOperationException("button must be left, right, or middle for click_desktop"),
        };
    }

    private static void SendMouseClick(int x, int y, string button, int clicks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SetCursorPos(x, y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed");
        }

        var (downFlag, upFlag) = button switch
        {
            "right" => (MouseEventRightDown, MouseEventRightUp),
            "middle" => (MouseEventMiddleDown, MouseEventMiddleUp),
            _ => (MouseEventLeftDown, MouseEventLeftUp),
        };

        var inputs = new Input[clicks * 2];
        for (var i = 0; i < clicks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inputs[i * 2] = CreateMouseInput(downFlag);
            inputs[(i * 2) + 1] = CreateMouseInput(upFlag);
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SendInput sent {sent} of {inputs.Length} mouse events");
        }
    }

    private static Input CreateMouseInput(uint flags)
    {
        return new Input
        {
            Type = InputMouse,
            MouseInput = new MouseInput
            {
                Flags = flags,
            },
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput MouseInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
