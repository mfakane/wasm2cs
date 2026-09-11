# Clang-produced fixture

`Algorithms.wasm` is compiled from `algorithms.c`; it is not hand-assembled.
Regenerate on Windows with `scripts/create-c-fixture.ps1`.
Then run `node scripts/create-c-golden.mjs` to regenerate the independent Node.js
oracle vectors consumed by the Unity Editor/IL2CPP test.

Toolchain: Unity 6000.6.0f1 WebGL LLVM, Clang 22.0.0git and LLD 22.0.0,
LLVM revision `e5071069e879dbf97a239cda009f8a3e3a4a7365`.
Binary SHA-256: `12ea81a5c81b17d983245cb8c6dc88e1a1621da3d5abe7c23ce8d173815f7057`.

The script pins the wasm32 target and MVP CPU, disables libc/builtins/vectorization,
uses `-O1`, explicitly exports the four functions and memory, and sets memory to
an initial 128 KiB and maximum 256 KiB. The generated binary is committed so normal
tests do not depend on a local C compiler.

`f(seed, length)` fills an internal buffer and combines its CRC32 and byte sum.
The independent Node.js engine and generated C# run identical calls and compare
return values and complete memory contents. `buffer_ptr`, `sum_bytes`, and `crc32`
also exercise host-side copies with the conventional `123456789` CRC test input.
