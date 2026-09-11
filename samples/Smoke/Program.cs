using Wasm2Cs.Generated;

var arithmetic = new Arithmetic();
Console.WriteLine($"add(20, 22) = {arithmetic.add(20, 22)}");
Console.WriteLine($"square(7) = {arithmetic.square(7)}");
Console.WriteLine($"add(int.MaxValue, 1) = {arithmetic.add(int.MaxValue, 1)}");
var algorithms = new Algorithms();
uint buffer = unchecked((uint)algorithms.buffer_ptr());
algorithms.WriteMemory(buffer, System.Text.Encoding.ASCII.GetBytes("123456789"));
uint crc = unchecked((uint)algorithms.crc32((int)buffer, 9));
if (crc != 0xcbf43926u) throw new Exception("CRC32 differs.");
Console.WriteLine($"Clang CRC32(123456789) = {crc:x8}");
