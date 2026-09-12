# Target vector fixture

`Vector.wasm.base64` is a small hand-assembled WebAssembly SIMD fixture. It
returns `(1, 2, 3, 4) + (10, 20, 30, 40)`, multiplied lane-wise by `(2, 2, 2, 2)`.
The fixture is used to verify the `unity-mathematics` backend in Unity Editor
and Windows x64 IL2CPP, alongside the .NET `Vector128<float>` check.
