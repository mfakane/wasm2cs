using Wasm2Cs.Generated;

Console.WriteLine($"add(20, 22) = {Arithmetic.add(20, 22)}");
Console.WriteLine($"square(7) = {Arithmetic.square(7)}");
Console.WriteLine($"add(int.MaxValue, 1) = {Arithmetic.add(int.MaxValue, 1)}");
