using System.Runtime.InteropServices;

namespace GuiaFotos.Core;

// Minimal CPU binding to the stable ONNX Runtime C API (API v20).
// Ordinals follow microsoft/onnxruntime v1.20.1 onnxruntime_c_api.h.
public sealed class NativeOnnx : IDisposable
{
    private readonly IntPtr library, api;
    private IntPtr env, allocator;
    private bool disposed;
    private readonly List<Model> models = [];
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr GetBase();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr GetApi(uint version);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr OutPointer(out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr EnvCreate(int level, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr PointerStatus(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Release(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SetInt(IntPtr value, int number);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SessionCreate(IntPtr env, byte[] model, nuint length, IntPtr options, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Count(IntPtr value, out nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Name(IntPtr value, nuint index, IntPtr allocator, out IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr IndexedInfo(IntPtr value, nuint index, out IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Info(IntPtr value, out IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Dims(IntPtr value, [Out] long[] shape, nuint length);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr ElementType(IntPtr info, out int type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr AllocatorFree(IntPtr allocator, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr TensorCreate(IntPtr allocator, long[] shape, nuint length, int type, out IntPtr tensor);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Run(IntPtr session, IntPtr options, IntPtr[] inputNames, IntPtr[] inputs, nuint inputCount, IntPtr[] outputNames, nuint outputCount, [Out] IntPtr[] outputs);
    private T Function<T>(int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(api, index * IntPtr.Size));
    private void Check(IntPtr status)
    {
        if (status == IntPtr.Zero) return;
        var text = Marshal.PtrToStringUTF8(Function<PointerStatus>(2)(status));
        Function<Release>(93)(status); throw new InvalidDataException("ONNX: " + text);
    }
    public NativeOnnx(string libraryPath)
    {
        library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        var apiBase = Marshal.GetDelegateForFunctionPointer<GetBase>(NativeLibrary.GetExport(library, "OrtGetApiBase"))();
        api = Marshal.GetDelegateForFunctionPointer<GetApi>(Marshal.ReadIntPtr(apiBase))(20);
        if (api == IntPtr.Zero) { NativeLibrary.Free(library); throw new NotSupportedException("ONNX Runtime API 20 indisponível."); }
        try { Check(Function<EnvCreate>(3)(3, "GuiaFotos-local", out env)); Check(Function<PointerStatus>(6)(env)); Check(Function<OutPointer>(78)(out allocator)); }
        catch { Dispose(); throw; }
    }
    public Model Load(string path) => Load(File.ReadAllBytes(path));
    public Model Load(byte[] bytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Check(Function<OutPointer>(10)(out var options)); IntPtr session = IntPtr.Zero;
        try
        {
            Check(Function<SetInt>(24)(options, Math.Clamp(Environment.ProcessorCount / 2, 1, 4)));
            Check(Function<SetInt>(23)(options, 99));
            Check(Function<SessionCreate>(8)(env, bytes, (nuint)bytes.Length, options, out session));
            var model = new Model(this, session); models.Add(model); return model;
        }
        catch { if (session != IntPtr.Zero) Function<Release>(95)(session); throw; }
        finally { Function<Release>(100)(options); }
    }
    public sealed class Model : IDisposable
    {
        private readonly NativeOnnx owner;
        private IntPtr session;
        private readonly IntPtr inputName;
        private readonly IntPtr[] outputNames;
        public string[] OutputNames { get; }
        public long[] InputShape { get; }
        internal Model(NativeOnnx owner, IntPtr session)
        {
            this.owner = owner; this.session = session;
            owner.Check(owner.Function<Count>(30)(session, out var inputs));
            if (inputs != 1) throw new NotSupportedException("Modelo deve ter uma entrada.");
            string ReadName(int index, nuint n)
            {
                owner.Check(owner.Function<Name>(index)(session, n, owner.allocator, out var pointer));
                try { return Marshal.PtrToStringUTF8(pointer)!; } finally { owner.Check(owner.Function<AllocatorFree>(76)(owner.allocator, pointer)); }
            }
            inputName = Marshal.StringToCoTaskMemUTF8(ReadName(36, 0));
            owner.Check(owner.Function<Count>(31)(session, out var count));
            OutputNames = Enumerable.Range(0, checked((int)count)).Select(i => ReadName(37, (nuint)i)).ToArray();
            outputNames = OutputNames.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            owner.Check(owner.Function<IndexedInfo>(33)(session, 0, out var type));
            try { owner.Check(owner.Function<Info>(55)(type, out var tensorInfo)); InputShape = Shape(tensorInfo); }
            finally { owner.Function<Release>(98)(type); }
        }
        private long[] Shape(IntPtr info)
        {
            owner.Check(owner.Function<Count>(61)(info, out var count)); var result = new long[checked((int)count)];
            owner.Check(owner.Function<Dims>(62)(info, result, count)); return result;
        }
        public Dictionary<string, float[]> Infer(float[] input, long[] shape)
        {
            ObjectDisposedException.ThrowIf(session == IntPtr.Zero, this);
            ObjectDisposedException.ThrowIf(owner.disposed, owner);
            if (shape.Aggregate(1L, (a, b) => checked(a * b)) != input.LongLength) throw new ArgumentException("Shape incompatível.");
            owner.Check(owner.Function<TensorCreate>(48)(owner.allocator, shape, (nuint)shape.Length, 1, out var tensor));
            var outputs = new IntPtr[outputNames.Length];
            try
            {
                owner.Check(owner.Function<Info>(51)(tensor, out var data)); Marshal.Copy(input, 0, data, input.Length);
                owner.Check(owner.Function<Run>(9)(session, IntPtr.Zero, [inputName], [tensor], 1, outputNames, (nuint)outputs.Length, outputs));
                var result = new Dictionary<string, float[]>();
                for (var i = 0; i < outputs.Length; i++)
                {
                    owner.Check(owner.Function<Info>(65)(outputs[i], out var info));
                    try
                    {
                        owner.Check(owner.Function<ElementType>(60)(info, out var elementType));
                        if (elementType != 1) throw new InvalidDataException("Saída ONNX deve ser float32.");
                        owner.Check(owner.Function<Count>(64)(info, out var length));
                        if (length > 20_000_000) throw new InvalidDataException("Saída ONNX excessiva.");
                        var values = new float[(int)length]; owner.Check(owner.Function<Info>(51)(outputs[i], out var pointer));
                        Marshal.Copy(pointer, values, 0, values.Length); result.Add(OutputNames[i], values);
                    }
                    finally { owner.Function<Release>(99)(info); }
                }
                return result;
            }
            finally
            {
                foreach (var output in outputs) if (output != IntPtr.Zero) owner.Function<Release>(96)(output);
                owner.Function<Release>(96)(tensor);
            }
        }
        public void Dispose()
        {
            if (session == IntPtr.Zero) return;
            owner.Function<Release>(95)(session); session = IntPtr.Zero;
            owner.models.Remove(this);
            Marshal.FreeCoTaskMem(inputName); foreach (var name in outputNames) Marshal.FreeCoTaskMem(name);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        foreach (var model in models.ToArray()) model.Dispose();
        disposed = true;
        if (env != IntPtr.Zero) Function<Release>(92)(env);
        NativeLibrary.Free(library);
    }
}
