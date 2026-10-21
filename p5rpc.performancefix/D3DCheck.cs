using System.Runtime.InteropServices;

namespace p5rpc.performancefix;

/// <summary>
/// Checks, on a throwaway device, that partial constant-buffer updates are safe to use: the driver reports
/// ConstantBufferPartialUpdate, both immediate and deferred contexts are their own ID3D11DeviceContext1 (so the
/// routine can call UpdateSubresource1 through the same pointer the game passes), and a partial DISCARD update made
/// the way the routine makes it really reaches the buffer on both kinds of context.
/// </summary>
public static unsafe class D3DCheck
{
    private static readonly Guid IidDeviceContext1 = new("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1");

    private const int BufferBytes = 0x1C00, FilledBytes = 0x400;

    public static (bool Ok, string Message) Run()
    {
        nint device = 0, immediate = 0, deferred = 0, buffer = 0, staging = 0;
        try
        {
            var hr = D3D11CreateDevice(0, 1 /* HARDWARE */, 0, 0, 0, 0, 7, out device, out _, out immediate);
            if (hr < 0 || device == 0) return (false, $"could not create a Direct3D 11 device to check the driver (0x{hr:X8})");
            var deviceVtbl = *(nint**)device;

            var options = stackalloc int[14]; // D3D11_FEATURE_DATA_D3D11_OPTIONS
            hr = ((delegate* unmanaged<nint, int, void*, uint, int>)deviceVtbl[33])(device, 5 /* D3D11_OPTIONS */, options, 14 * 4);
            if (hr < 0) return (false, $"the driver did not answer the Direct3D 11.1 options query (0x{hr:X8})");
            if (options[6] == 0) return (false, "the graphics driver does not support partial constant-buffer updates");

            ((delegate* unmanaged<nint, uint, nint*, int>)deviceVtbl[27])(device, 0, &deferred); // CreateDeferredContext
            foreach (var (name, context) in new[] { ("immediate", immediate), ("deferred", deferred) })
            {
                if (context == 0) return (false, $"no {name} context to check");
                nint context1 = 0;
                var iid = IidDeviceContext1;
                hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)context)[0])(context, &iid, &context1);
                if (hr < 0 || context1 == 0) return (false, $"the {name} context is not an ID3D11DeviceContext1");
                Release(context1);
                if (context1 != context) return (false, $"the {name} context's ID3D11DeviceContext1 is a different object");
            }

            buffer = CreateBuffer(device, BufferBytes, 0 /* DEFAULT */, 0x4 /* CONSTANT_BUFFER */, 0);
            staging = CreateBuffer(device, BufferBytes, 3 /* STAGING */, 0, 0x20000 /* CPU_READ */);
            if (buffer == 0 || staging == 0) return (false, "could not create test buffers to check the driver");

            var box = stackalloc uint[6] { 0, 0, 0, FilledBytes, 1, 1 }; // D3D11_BOX
            foreach (var (name, context, value) in new[] { ("immediate", immediate, (byte)0x22), ("deferred", deferred, (byte)0x33) })
            {
                var old = new byte[BufferBytes]; Array.Fill(old, (byte)0x11);
                var data = new byte[BufferBytes]; Array.Fill(data, value);
                fixed (byte* o = old, d = data)
                {
                    ((delegate* unmanaged<nint, nint, uint, nint, nint, uint, uint, void>)(*(nint**)immediate)[UploadStub.UpdateSubresourceSlot])(
                        immediate, buffer, 0, 0, (nint)o, 0, 0);
                    ((delegate* unmanaged<nint, nint, uint, uint*, nint, uint, uint, uint, void>)(*(nint**)context)[UploadStub.UpdateSubresource1Slot])(
                        context, buffer, 0, box, (nint)d, 0, 0, UploadStub.CopyDiscard);
                }

                if (context == deferred)
                {
                    nint list = 0;
                    hr = ((delegate* unmanaged<nint, int, nint*, int>)(*(nint**)deferred)[114])(deferred, 0, &list); // FinishCommandList
                    if (hr < 0 || list == 0) return (false, $"could not finish a test command list (0x{hr:X8})");
                    ((delegate* unmanaged<nint, nint, int, void>)(*(nint**)immediate)[58])(immediate, list, 0); // ExecuteCommandList
                    Release(list);
                }

                var contents = ReadBack(immediate, buffer, staging);
                if (contents == null) return (false, "could not read a test buffer back");
                if (contents.AsSpan(0, FilledBytes).IndexOfAnyExcept(value) >= 0)
                    return (false, $"the graphics driver ignored a partial constant-buffer update on a {name} context");
            }

            return (true, "the driver supports partial constant-buffer updates");
        }
        catch (Exception exception)
        {
            return (false, $"driver check failed: {exception.Message}");
        }
        finally
        {
            if (staging != 0) Release(staging);
            if (buffer != 0) Release(buffer);
            if (deferred != 0) Release(deferred);
            if (immediate != 0) Release(immediate);
            if (device != 0) Release(device);
        }
    }

    private static nint CreateBuffer(nint device, uint bytes, uint usage, uint bind, uint cpuAccess)
    {
        var desc = stackalloc uint[6] { bytes, usage, bind, cpuAccess, 0, 0 }; // D3D11_BUFFER_DESC
        nint buffer = 0;
        var hr = ((delegate* unmanaged<nint, uint*, nint, nint*, int>)(*(nint**)device)[3])(device, desc, 0, &buffer); // CreateBuffer
        return hr >= 0 ? buffer : 0;
    }

    private static byte[]? ReadBack(nint immediate, nint buffer, nint staging)
    {
        var vtbl = *(nint**)immediate;
        ((delegate* unmanaged<nint, nint, nint, void>)vtbl[47])(immediate, staging, buffer); // CopyResource
        var mapped = stackalloc nint[2]; // D3D11_MAPPED_SUBRESOURCE
        if (((delegate* unmanaged<nint, nint, uint, int, uint, nint*, int>)vtbl[14])(immediate, staging, 0, 1 /* READ */, 0, mapped) < 0)
            return null;
        var contents = new ReadOnlySpan<byte>((void*)mapped[0], BufferBytes).ToArray();
        ((delegate* unmanaged<nint, nint, uint, void>)vtbl[15])(immediate, staging, 0); // Unmap
        return contents;
    }

    private static void Release(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels,
        uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);
}
