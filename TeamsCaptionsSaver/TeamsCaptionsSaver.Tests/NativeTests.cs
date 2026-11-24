using System.Reflection;
using System.Runtime.InteropServices;
using TeamsCaptionsSaver.Desktop.Capture;
using Xunit;

namespace TeamsCaptionsSaver.Tests;

public class NativeTests
{
    [Fact]
    public void Input_struct_has_the_size_SendInput_expects()
    {
        var input = typeof(Native).GetNestedType("INPUT", BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(input));
    }
}
