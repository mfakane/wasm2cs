using Wasm2Cs.Generated;

var arithmetic = new Arithmetic();
Console.WriteLine($"add(20, 22) = {arithmetic.add(20, 22)}");
Console.WriteLine($"square(7) = {arithmetic.square(7)}");
Console.WriteLine($"add(int.MaxValue, 1) = {arithmetic.add(int.MaxValue, 1)}");
