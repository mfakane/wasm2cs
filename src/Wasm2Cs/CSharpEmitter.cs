using System.Globalization;
using System.Text;
namespace Wasm2Cs;
internal static class CSharpEmitter
{
    private const int InitializationDataChunkSize = 48 * 1024;
    private const int InitializationElementChunkSize = 256;
    private const int FunctionChunkCharacters = 64 * 1024;

    public static string Emit(Module module, string className, LoweringPlan lowering)
        => string.Concat(EmitSourceSequence(module, className, lowering).Select(source => source.Text));

    public static IReadOnlyList<GeneratedSource> EmitSources(Module module, string className, LoweringPlan lowering,
        CancellationToken cancellationToken = default) => EmitSourceSequence(module, className, lowering, cancellationToken).ToArray();

    public static IEnumerable<GeneratedSource> EmitSourceSequence(Module module, string className, LoweringPlan lowering,
        CancellationToken cancellationToken = default, bool streamFunctions = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new StringBuilder(Header(lowering.Profile));
        source.Append("public sealed partial class @").Append(className).Append("\n{\n");
        source.Append(I32Operations.Helpers);
        bool hasFunctionReferences = module.Tables.Count != 0 || module.Elements.Any(e => e.Values.Any(v => v.FunctionIndex.HasValue)) ||
            module.HasFunctionReferences;
        if (hasFunctionReferences) source.Append(TableDelegates(module, lowering));
        if (module.HasI64Operations) source.Append(I64Operations.Helpers);
        if (module.HasFloatHelpers)
            source.Append(FloatOperations.Helpers);
        bool moduleHasVector = module.HasSimd ||
            module.Types.Any(t => t.Parameters.Contains(ValueType.V128) || t.Results.Contains(ValueType.V128)) ||
            module.Bodies.Any(f => f.Locals.Contains(ValueType.V128) || f.Instructions.Any(i => i.Opcode == 0xfd)) ||
            module.Globals.Any(g => g.Type == ValueType.V128);
        if (module.HasFloatHelpers || moduleHasVector)
            source.Append(lowering.ExtraHelpers(module));
        if (module.HasConversionOperations)
            source.Append(ConversionOperations.Helpers);
        if (module.Memory != null) source.Append(lowering.MemoryHelpers(module));
        for(int g=0;g<module.Globals.Count;g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module.Globals[g].Imported)
                source.Append($"    private readonly global::Wasm2Cs.WasmGlobal __wasm_import_global{g};\n");
            else
                source.Append($"    private {TypeName(module.Globals[g].Type, lowering)} __wasm_G{g};\n");
        }
        for (int t=0;t<module.Tables.Count;t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Append($"    private global::Wasm2Cs.WasmTable __wasm_table{t};\n");
        }
        for (int t=0;t<module.Tags.Count;t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Append(module.Tags[t].Imported
                ? $"    private readonly global::Wasm2Cs.WasmTag __wasm_tag{t};\n"
                : $"    private global::Wasm2Cs.WasmTag __wasm_tag{t};\n");
        }
        if (module.Tables.Count != 0) source.Append(TableOperations.Helpers);
        for (int g=0;g<module.Globals.Count;g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module.Globals[g].Imported) source.Append(GlobalAccessor(module, g, lowering));
        }
        for (int d=0;d<module.Data.Count;d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module.Data[d].Passive || module.Data[d].Bytes.Length > InitializationDataChunkSize)
                source.Append($"    private byte[] __wasm_D{d};\n");
        }
        bool floatImports = module.Imports.Select((_, i) => module.FunctionSignature(i)).Any(t => t.Parameters.Any(IsFloat) || t.Results.Any(IsFloat));
        for (int i=0;i<module.Imports.Count;i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = module.FunctionSignature(i);
            string parameters = string.Join(", ", signature.Parameters.Select((type, p) => $"{TypeName(type, lowering)} v{p}"));
            // Base64 keeps arbitrary UTF-8 import names out of C# syntax and comments.
            source.Append($"    // Import {i}: module/name UTF-8 base64 {Convert.ToBase64String(Encoding.UTF8.GetBytes(module.Imports[i].ModuleName))}/{Convert.ToBase64String(Encoding.UTF8.GetBytes(module.Imports[i].Name))}\n");
            source.Append($"    public delegate {BoundaryResultType(signature.Results, false, lowering)} __wasm_Import{i}({BoundaryParameters(signature.Parameters, false, lowering)});\n");
            if (floatImports) source.Append($"    public delegate {BoundaryResultType(signature.Results, true, lowering)} __wasm_BitsImport{i}({BoundaryParameters(signature.Parameters, true, lowering)});\n");
            source.Append($"    private __wasm_{(floatImports ? "BitsImport" : "Import")}{i} __wasm_host{i};\n");
            string call = $"__wasm_host{i}({string.Join(", ",signature.Parameters.Select((t,p) => ToBoundary($"v{p}", t, floatImports)))})";
            source.Append($"    private {ResultType(signature.Results, lowering)} __wasm_F{i}({parameters}) {{ if (__wasm_host{i} == null) throw new global::Wasm2Cs.WasmImportException(\"")
                .Append(EscapeString(module.Imports[i].ModuleName)).Append("\", \"")
                .Append(EscapeString(module.Imports[i].Name)).Append("\", new object[] { ")
                .Append(string.Join(", ", signature.Parameters.Select((_, p) => $"v{p}")))
                .Append(" }); ").Append(ReturnBoundary(call, signature.Results, true, floatImports)).Append(" }\n");
        }
        var bindingNames = BindingMemberNames(module);
        source.Append("    public sealed class Bindings\n    {\n");
        for (int i=0;i<module.ImportBindings.Count;i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = module.ImportBindings[i];
            string member = bindingNames[i];
            source.Append("        // Import UTF-8 base64 ")
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.ModuleName))).Append('/')
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.Name))).Append("\n");
            if (binding.Kind == 0)
                source.Append($"        public __wasm_Import{binding.Index} {member} {{ get; set; }}\n");
            else if (binding.Kind == 1)
                source.Append($"        public global::Wasm2Cs.WasmTable {member} {{ get; set; }}\n");
            else if (binding.Kind == 2)
                source.Append($"        public global::Wasm2Cs.WasmMemory {member} {{ get; set; }}\n");
            else if (binding.Kind == 3)
                source.Append($"        public global::Wasm2Cs.WasmGlobal {member} {{ get; set; }}\n");
            else if (binding.Kind == 4)
                source.Append($"        public global::Wasm2Cs.WasmTag {member} {{ get; set; }}\n");
        }
        source.Append("    }\n");
        for (int e=0;e<module.Elements.Count;e++)
            if (module.Elements[e].Passive || (!module.Elements[e].Declarative && module.Elements[e].Values.Length > InitializationElementChunkSize))
                source.Append($"    private object[] __wasm_E{e};\n");
        source.Append($"    public @{className}({ConstructorParameters(module, false)})\n    {{\n");
        for (int i=0;i<module.Imports.Count;i++)
        {
            source.Append($"        if (import{i} == null) throw new global::System.ArgumentNullException(\"import{i}\");\n");
            if (!floatImports) source.Append($"        __wasm_host{i} = import{i};\n");
            else
            {
                var signature = module.FunctionSignature(i);
                string call = $"import{i}({string.Join(", ", signature.Parameters.Select((t,p) => ToBoundary(FromBoundary($"v{p}", t, true), t, false)))})";
                string returned = ReturnConverted(call, signature.Results, (value,t) => ToBoundary(FromBoundary(value,t,false),t,true));
                source.Append($"        __wasm_host{i} = ({string.Join(", ",Enumerable.Range(0,signature.Parameters.Length).Select(p=>$"v{p}"))}) => {{ {returned} }};\n");
            }
        }
        foreach (var binding in module.ImportBindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string parameter = ImportParameterName(binding);
            if (binding.Kind == 2)
            {
                source.Append($"        __wasm_memory = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
                source.Append($"        __wasm_memory.ValidateImport({module.Memory!.Minimum}, {NullableInt(module.Memory.Maximum)});\n");
            }
            else if (binding.Kind == 3)
            {
                var global = module.Globals[binding.Index];
                source.Append($"        __wasm_import_global{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
                source.Append($"        __wasm_import_global{binding.Index}.Validate({WasmTypeName(global.Type)}, {(global.Mutable ? "true" : "false")});\n");
            }
            else if (binding.Kind == 1)
            {
                var table = module.Tables[binding.Index];
                source.Append($"        __wasm_table{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
                source.Append($"        __wasm_table{binding.Index}.ValidateImport({WasmTypeName(table.ElementType)}, {table.Minimum}, {NullableInt(table.Maximum)});\n");
            }
            else if (binding.Kind == 4)
            {
                var tag = module.Tags[binding.Index];
                source.Append($"        __wasm_tag{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
                source.Append($"        __wasm_tag{binding.Index}.Validate({TagParameterTypes(tag.Signature)});\n");
            }
        }
        source.Append("        __wasm_Initialize();\n    }\n");
        source.Append($"    public @{className}(Bindings bindings)\n    {{\n        if (bindings == null) throw new global::System.ArgumentNullException(\"bindings\");\n");
        for (int i=0;i<module.Imports.Count;i++)
        {
            var bindingIndex = module.ImportBindings.FindIndex(binding => binding.Kind == 0 && binding.Index == i);
            string parameter = $"bindings.{bindingNames[bindingIndex]}";
            if (!floatImports) source.Append($"        __wasm_host{i} = {parameter};\n");
            else
            {
                var signature = module.FunctionSignature(i);
                string call = $"{parameter}({string.Join(", ", signature.Parameters.Select((t,p) => ToBoundary(FromBoundary($"v{p}", t, true), t, false)))})";
                string returned = ReturnConverted(call, signature.Results, (value,t) => ToBoundary(FromBoundary(value,t,false),t,true));
                source.Append($"        if ({parameter} != null) __wasm_host{i} = ({string.Join(", ",Enumerable.Range(0,signature.Parameters.Length).Select(p=>$"v{p}"))}) => {{ {returned} }};\n");
            }
        }
        foreach (var binding in module.ImportBindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string parameter = $"bindings.{bindingNames[module.ImportBindings.IndexOf(binding)]}";
            if (binding.Kind == 2)
            {
                source.Append($"        __wasm_memory = {parameter} ?? throw new global::System.ArgumentNullException(\"{binding.Name}\");\n");
                source.Append($"        __wasm_memory.ValidateImport({module.Memory!.Minimum}, {NullableInt(module.Memory.Maximum)});\n");
            }
            else if (binding.Kind == 3)
            {
                var global = module.Globals[binding.Index];
                source.Append($"        __wasm_import_global{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{binding.Name}\");\n");
                source.Append($"        __wasm_import_global{binding.Index}.Validate({WasmTypeName(global.Type)}, {(global.Mutable ? "true" : "false")});\n");
            }
            else if (binding.Kind == 1)
            {
                var table = module.Tables[binding.Index];
                source.Append($"        __wasm_table{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{binding.Name}\");\n");
                source.Append($"        __wasm_table{binding.Index}.ValidateImport({WasmTypeName(table.ElementType)}, {table.Minimum}, {NullableInt(table.Maximum)});\n");
            }
            else if (binding.Kind == 4)
            {
                var tag = module.Tags[binding.Index];
                source.Append($"        __wasm_tag{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{binding.Name}\");\n");
                source.Append($"        __wasm_tag{binding.Index}.Validate({TagParameterTypes(tag.Signature)});\n");
            }
        }
        source.Append("        __wasm_Initialize();\n    }\n");
        if (floatImports)
        {
            string parameters = ConstructorParameters(module, true);
            string arguments = string.Join(", ", module.ImportBindings.Select(ImportParameterName));
            source.Append($"    public static @{className} __wasm_FromBits({parameters}) {{ return new @{className}({arguments}, true); }}\n");
            source.Append($"    private @{className}({parameters}, bool __wasm_bits)\n    {{\n");
            for (int i=0;i<module.Imports.Count;i++)
            {
                var parameter = ImportParameterName(module.ImportBindings.First(b => b.Kind == 0 && b.Index == i));
                source.Append($"        __wasm_host{i} = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
            }
            foreach (var binding in module.ImportBindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string parameter = ImportParameterName(binding);
                if (binding.Kind == 2)
                {
                    source.Append($"        __wasm_memory = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
                    source.Append($"        __wasm_memory.ValidateImport({module.Memory!.Minimum}, {NullableInt(module.Memory.Maximum)});\n");
                }
                else if (binding.Kind == 3)
                {
                    var global = module.Globals[binding.Index];
                    source.Append($"        __wasm_import_global{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
                    source.Append($"        __wasm_import_global{binding.Index}.Validate({WasmTypeName(global.Type)}, {(global.Mutable ? "true" : "false")});\n");
                }
                else if (binding.Kind == 1)
                {
                    var table = module.Tables[binding.Index];
                    source.Append($"        __wasm_table{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
                    source.Append($"        __wasm_table{binding.Index}.ValidateImport({WasmTypeName(table.ElementType)}, {table.Minimum}, {NullableInt(table.Maximum)});\n");
                }
                else if (binding.Kind == 4)
                {
                    var tag = module.Tags[binding.Index];
                    source.Append($"        __wasm_tag{binding.Index} = {parameter} ?? throw new global::System.ArgumentNullException(\"{parameter}\");\n");
                    source.Append($"        __wasm_tag{binding.Index}.Validate({TagParameterTypes(tag.Signature)});\n");
                }
            }
            source.Append("        __wasm_Initialize();\n    }\n");
        }
        source.Append("    private void __wasm_Initialize()\n    {\n");
        var initializationLines = new List<string>();
        var initializationSources = new List<GeneratedSource>();
        if (module.Memory != null && !module.Memory.Imported)
            source.Append($"        __wasm_memory = new global::Wasm2Cs.WasmMemory({module.Memory.Minimum}, {NullableInt(module.Memory.Maximum)}, 4096);\n");
        for (int t=0;t<module.Tables.Count;t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!module.Tables[t].Imported)
                source.Append($"        __wasm_table{t} = new global::Wasm2Cs.WasmTable({WasmTypeName(module.Tables[t].ElementType)}, {module.Tables[t].Minimum}, {NullableInt(module.Tables[t].Maximum)});\n");
        }
        for (int t=0;t<module.Tags.Count;t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!module.Tags[t].Imported)
                source.Append($"        __wasm_tag{t} = new global::Wasm2Cs.WasmTag({TagParameterTypes(module.Tags[t].Signature)});\n");
        }
        for(int g=0;g<module.Globals.Count;g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!module.Globals[g].Imported)
                source.Append($"        __wasm_G{g} = {ConstantExpression(module.Globals[g].InitialValue!, module)};\n");
        }
        for (int d=0;d<module.Data.Count;d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = module.Data[d];
            if (data.Passive || data.Bytes.Length > InitializationDataChunkSize)
            {
                initializationLines.Add($"        __wasm_D{d} = new byte[{data.Bytes.Length}];\n");
                AppendDataChunks(initializationLines, $"__wasm_D{d}", data.Bytes);
                if (!data.Passive)
                {
                    string offset = ConstantExpression(data.OffsetExpression ?? new ConstantValue(ValueType.I32, data.Offset), module);
                    initializationLines.Add($"        WriteMemory(unchecked((uint)({offset})), __wasm_D{d});\n");
                }
            }
            else
            {
                string offset = ConstantExpression(data.OffsetExpression ?? new ConstantValue(ValueType.I32, data.Offset), module);
                initializationLines.Add($"        WriteMemory(unchecked((uint)({offset})), global::System.Convert.FromBase64String(\"{Convert.ToBase64String(data.Bytes)}\"));\n");
            }
        }
        for (int e=0;e<module.Elements.Count;e++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = module.Elements[e];
            if (element.Passive || !element.Declarative)
                AppendElementChunks(initializationLines, module, element, lowering, e);
        }
        AppendInitializationChunks(source, initializationLines, initializationSources, className, lowering.Profile, cancellationToken);
        if (module.Start.HasValue) source.Append($"        __wasm_F{module.Start.Value}();\n");
        source.Append("    }\n");
        foreach (var export in module.Exports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte kind = module.ExportKinds[export.Key];
            string exportName = ExportNames.For(export.Key);
            source.Append($"    // WASM export UTF-8 base64 {Convert.ToBase64String(Encoding.UTF8.GetBytes(export.Key))} -> {exportName}\n");
            if (kind == 1)
            {
                source.Append($"    public global::Wasm2Cs.WasmTable @{exportName} {{ get {{ return __wasm_table{export.Value}; }} }}\n");
                continue;
            }
            if (kind == 4)
            {
                source.Append($"    public global::Wasm2Cs.WasmTag @{exportName} {{ get {{ return __wasm_tag{export.Value}; }} }}\n");
                continue;
            }
            if (kind == 2)
            {
                source.Append($"    public global::Wasm2Cs.WasmMemory @{exportName} {{ get {{ return __wasm_memory; }} }}\n");
                continue;
            }
            bool floating = kind == 3 ? IsFloat(module.Globals[export.Value].Type) :
                module.FunctionSignature(export.Value).Parameters.Any(IsFloat) || module.FunctionSignature(export.Value).Results.Any(IsFloat);
            foreach (bool bits in floating ? new[] { false, true } : new[] { false })
            {
                string name = bits ? "__wasm_bits_" + exportName : exportName;
                if (kind == 3)
                {
                    var global = module.Globals[export.Value];
                    source.Append($"    public {BoundaryTypeName(global.Type,bits,lowering)} @{name} {{ get {{ return {ToBoundary(GlobalExpression(module, export.Value),global.Type,bits)}; }}");
                    if (global.Mutable)
                    {
                        string value = FromBoundary("value", global.Type, bits);
                        source.Append($" set {{ {GlobalSet(module, export.Value, value)} }}");
                    }
                    source.Append(" }\n");
                    continue;
                }
                var signature = module.FunctionSignature(export.Value);
                string call = $"__wasm_F{export.Value}({string.Join(", ",signature.Parameters.Select((t,p)=>FromBoundary($"v{p}",t,bits)))})";
                source.Append($"    public {BoundaryResultType(signature.Results,bits,lowering)} @{name}({BoundaryParameters(signature.Parameters,bits,lowering)})\n    {{\n        {ReturnBoundary(call,signature.Results,false,bits)}\n    }}\n");
            }
        }
        yield return new GeneratedSource($"{className}.g.cs", source.Append("}\n}\n").ToString());
        foreach (var initializationSource in initializationSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return initializationSource;
        }
        for (int i=0;i<module.Bodies.Count;i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module.MetadataOnly && (i & 15) == 0) GC.Collect();
            var function = module.MetadataOnly
                ? Decoder.DecodeBody(new Reader(module.BodyData[i]), module.Bodies[i].Signature, module.Types, cancellationToken)
                : module.Bodies[i];
            if (module.MetadataOnly) Validator.ValidateBody(module, function, i, cancellationToken);
            var tempLayout = CreateTempLayout(module, function, lowering);
            var localLayout = CreateLocalLayout(function, lowering);
            var functionSource = FunctionPrefix(function, className, lowering, tempLayout, localLayout, i + module.Imports.Count);
            if (streamFunctions)
            {
                yield return new GeneratedSource($"{className}.Functions.{i:D4}.g.cs", functionSource.ToString());
                functionSource.Clear();
                functionSource.Capacity = 0;
                foreach (var chunk in EmitBodyChunks(module, function, lowering, cancellationToken, tempLayout, localLayout.Names))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new GeneratedSource($"{className}.Functions.{i:D4}.g.cs", chunk);
                }
                yield return new GeneratedSource($"{className}.Functions.{i:D4}.g.cs", "    }\n}\n}\n");
                functionSource = null!;
            }
            else
            {
                foreach (var chunk in EmitBodyChunks(module, function, lowering, cancellationToken, tempLayout, localLayout.Names))
                    functionSource.Append(chunk);
                functionSource.Append("    }\n}\n}\n");
                string functionText = functionSource.ToString();
                functionSource.Clear();
                functionSource.Capacity = 0;
                yield return new GeneratedSource($"{className}.Functions.{i:D4}.g.cs", functionText);
            }
            if (module.MetadataOnly)
            {
                function.Instructions.Clear();
                function.Instructions.Capacity = 0;
            }
        }
    }
    private static string Header(WasmTargetProfile profile) =>
        $"// <auto-generated/>\n// wasm2cs target profile: {WasmTargetProfiles.Name(profile)}\n#pragma warning disable CS0168, CS0219, CS0414, CS0162, CS0164 // Valid WASM may contain unused exceptions/values/globals, unreachable code and labels.\nnamespace Wasm2Cs.Generated\n{{\n";
    private sealed class TempSlot(string type, string name)
    {
        public string Type { get; } = type;
        public string Name { get; } = name;
        public int Count { get; set; }
    }
    private sealed class TempLayout
    {
        public Dictionary<string, TempSlot> ByType { get; } = new Dictionary<string, TempSlot>(StringComparer.Ordinal);
        public List<TempSlot> Slots { get; } = new List<TempSlot>();
        public int Capacity { get; init; }
        public void Add(string type)
        {
            if (ByType.ContainsKey(type)) return;
            var slot = new TempSlot(type, $"__wasm_t{Slots.Count}");
            ByType.Add(type, slot);
            Slots.Add(slot);
        }
    }
    private sealed class LocalSlot(string type, string name)
    {
        public string Type { get; } = type;
        public string Name { get; } = name;
        public int Count { get; set; }
    }
    private sealed record LocalLayout(string[] Names, IReadOnlyList<LocalSlot> Slots);
    private static TempLayout CreateTempLayout(Module module, Function function, LoweringPlan lowering)
    {
        int maxArity = module.Types.Count == 0 ? 0 : module.Types.Max(type => type.Parameters.Length + type.Results.Length);
        int perInstruction = checked(maxArity * 3 + 4);
        var layout = new TempLayout
        {
            Capacity = Math.Max(64, checked(function.Instructions.Count * perInstruction + 64))
        };
        layout.Add("object");
        layout.Add("global::System.Delegate");
        layout.Add("int");
        layout.Add("long");
        if (module.HasFloatHelpers)
        {
            layout.Add(TypeName(ValueType.F32, lowering));
            layout.Add(TypeName(ValueType.F64, lowering));
        }
        bool hasVector = module.Types.Any(t => t.Parameters.Contains(ValueType.V128) || t.Results.Contains(ValueType.V128)) ||
            function.Locals.Contains(ValueType.V128) || function.Instructions.Any(i => i.Opcode == 0xfd);
        if (hasVector && (lowering.Profile is WasmTargetProfile.DotNetVector or WasmTargetProfile.UnityMathematics))
            layout.Add(TypeName(ValueType.V128, lowering));
        void AddCallTemps(Signature signature)
        {
            if (signature.Results.Length > 1) layout.Add(ResultType(signature.Results, lowering));
        }
        AddCallTemps(function.Signature);
        foreach (var instruction in function.Instructions)
        {
            if (instruction.Opcode == 0x10) AddCallTemps(module.FunctionSignature(instruction.Operand));
            else if (instruction.Opcode == 0x11)
            {
                var signature = module.Types[instruction.Operand];
                AddCallTemps(signature);
                layout.Add(TableDelegateName(module, signature, lowering));
            }
            else if (instruction.Opcode == 0xd2 && instruction.Reference?.FunctionIndex is int functionIndex)
                layout.Add(TableDelegateName(module, module.FunctionSignature(functionIndex), lowering));
        }
        return layout;
    }
    private static LocalLayout CreateLocalLayout(Function function, LoweringPlan lowering)
    {
        var names = new string[function.Locals.Length];
        var byType = new Dictionary<string, LocalSlot>(StringComparer.Ordinal);
        var slots = new List<LocalSlot>();
        int parameters = function.Signature.Parameters.Length;
        for (int i = 0; i < parameters; i++) names[i] = $"v{i}";
        for (int i = parameters; i < function.Locals.Length; i++)
        {
            string type = TypeName(function.Locals[i], lowering);
            if (!byType.TryGetValue(type, out var slot))
            {
                slot = new LocalSlot(type, $"__wasm_l{slots.Count}");
                byType.Add(type, slot);
                slots.Add(slot);
            }
            names[i] = $"{slot.Name}[{slot.Count}]";
            slot.Count++;
        }
        return new LocalLayout(names, slots);
    }
    private static StringBuilder FunctionPrefix(Function function, string className, LoweringPlan lowering,
        TempLayout temps, LocalLayout locals, int functionIndex)
    {
        var source = new StringBuilder(Header(lowering.Profile));
        source.Append("public sealed partial class @").Append(className).Append("\n{\n");
        source.Append("    private ").Append(ResultType(function.Signature.Results, lowering))
            .Append(" __wasm_F").Append(functionIndex).Append('(')
            .Append(string.Join(", ", function.Signature.Parameters.Select((type, p) => $"{TypeName(type, lowering)} v{p}")))
            .Append(")\n    {\n");
        foreach (var slot in locals.Slots)
            source.Append("        ").Append(slot.Type).Append("[] ").Append(slot.Name)
                .Append(" = new ").Append(slot.Type).Append('[').Append(slot.Count).Append("];\n");
        foreach (var slot in temps.Slots)
            source.Append("        ").Append(slot.Type).Append("[] ").Append(slot.Name)
                .Append(" = new ").Append(slot.Type).Append('[').Append(temps.Capacity).Append("];\n");
        return source;
    }
    private static void AppendDataChunks(List<string> lines, string passiveName, byte[] bytes)
    {
        const int chunkSize = InitializationDataChunkSize;
        if (bytes.Length == 0) return;
        for (int start = 0; start < bytes.Length; start += chunkSize)
        {
            int count = Math.Min(chunkSize, bytes.Length - start);
            string encoded = Convert.ToBase64String(bytes, start, count);
            lines.Add($"        global::System.Buffer.BlockCopy(global::System.Convert.FromBase64String(\"{encoded}\"), 0, {passiveName}, {start}, {count});\n");
        }
    }
    private static void AppendElementChunks(List<string> lines, Module module, ElementSegment element,
        LoweringPlan lowering, int elementIndex)
    {
        const int chunkSize = InitializationElementChunkSize;
        bool split = element.Values.Length > chunkSize;
        if (!split)
        {
            string values = string.Join(", ", element.Values.Select(v => ReferenceExpression(module, v, lowering)));
            if (element.Passive)
                lines.Add($"        __wasm_E{elementIndex} = new object[] {{ {values} }};\n");
            else
            {
                string offset = ConstantExpression(element.OffsetExpression!, module);
                lines.Add($"        __wasm_TableInit(__wasm_table{element.TableIndex}, new object[] {{ {values} }}, unchecked((int)({offset})), 0, {element.Values.Length});\n");
            }
            return;
        }
        lines.Add($"        __wasm_E{elementIndex} = new object[{element.Values.Length}];\n");
        string? activeOffset = element.Passive ? null : ConstantExpression(element.OffsetExpression!, module);
        for (int start = 0; start < element.Values.Length; start += chunkSize)
        {
            int count = Math.Min(chunkSize, element.Values.Length - start);
            string values = string.Join(", ", element.Values.Skip(start).Take(count).Select(v => ReferenceExpression(module, v, lowering)));
            lines.Add($"        global::System.Array.Copy(new object[] {{ {values} }}, 0, __wasm_E{elementIndex}, {start}, {count});\n");
        }
        if (!element.Passive)
            lines.Add($"        __wasm_TableInit(__wasm_table{element.TableIndex}, __wasm_E{elementIndex}, unchecked((int)({activeOffset})), 0, {element.Values.Length});\n");
    }
    private static void AppendInitializationChunks(StringBuilder scaffold, List<string> lines, List<GeneratedSource> sources,
        string className, WasmTargetProfile profile, CancellationToken cancellationToken)
    {
        const int maxChunkCharacters = 96 * 1024;
        if (lines.Count == 0) return;
        var chunks = new List<string>();
        var chunk = new StringBuilder();
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Length != 0 && chunk.Length + line.Length > maxChunkCharacters)
            {
                chunks.Add(chunk.ToString());
                chunk.Clear();
            }
            chunk.Append(line);
        }
        if (chunk.Length != 0) chunks.Add(chunk.ToString());
        for (int i = 0; i < chunks.Count; i++)
        {
            scaffold.Append($"        __wasm_InitializeData{i}();\n");
            var source = new StringBuilder(Header(profile));
            source.Append("public sealed partial class @").Append(className).Append("\n{\n");
            source.Append("    private void __wasm_InitializeData").Append(i).Append("()\n    {\n");
            source.Append(chunks[i]).Append("    }\n}\n}\n");
            sources.Add(new GeneratedSource($"{className}.Initialization.{i:D4}.g.cs", source.ToString()));
        }
    }
    private static bool IsFloat(ValueType type) => type == ValueType.F32 || type == ValueType.F64;
    private static string ImportParameterName(ImportBinding binding) => binding.Kind switch
    {
        0 => $"import{binding.Index}",
        1 => $"table{binding.Index}",
        2 => "memory0",
        3 => $"global{binding.Index}",
        4 => $"tag{binding.Index}",
        _ => throw new WasmException("Unsupported import kind.")
    };
    private static string[] BindingMemberNames(Module module)
    {
        var names = new string[module.ImportBindings.Count];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < module.ImportBindings.Count; i++)
        {
            var binding = module.ImportBindings[i];
            string name = ExportNames.For($"import_{binding.ModuleName}_{binding.Name}");
            if (!used.Add(name))
            {
                int suffix = 2;
                string candidate;
                do candidate = name + "_" + suffix++; while (!used.Add(candidate));
                name = candidate;
            }
            names[i] = name;
        }
        return names;
    }
    private static string EscapeString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n");
    private static string ConstructorParameters(Module module, bool bits) => string.Join(", ", module.ImportBindings.Select(binding =>
        binding.Kind == 0
            ? $"__wasm_{(bits ? "BitsImport" : "Import")}{binding.Index} {ImportParameterName(binding)}"
            : binding.Kind == 1
                ? $"global::Wasm2Cs.WasmTable {ImportParameterName(binding)}"
                : binding.Kind == 2
                ? $"global::Wasm2Cs.WasmMemory {ImportParameterName(binding)}"
                : binding.Kind == 3
                ? $"global::Wasm2Cs.WasmGlobal {ImportParameterName(binding)}"
                : $"global::Wasm2Cs.WasmTag {ImportParameterName(binding)}"));
    private static string TableDelegates(Module module, LoweringPlan lowering)
    {
        var source = new StringBuilder();
        for (int i = 0; i < module.Types.Count; i++)
        {
            if (module.Types.Take(i).Any(type => SameSignature(type, module.Types[i]))) continue;
            var signature = module.Types[i];
            if (UsesBuiltinDelegate(signature)) continue;
            source.Append($"    private delegate {ResultType(signature.Results, lowering)} __wasm_TableCall{i}(")
                .Append(string.Join(", ", signature.Parameters.Select((type, p) => $"{TypeName(type, lowering)} v{p}")))
                .Append(");\n");
        }
        return source.ToString();
    }
    private static bool SameSignature(Signature left, Signature right) =>
        left.Parameters.SequenceEqual(right.Parameters) && left.Results.SequenceEqual(right.Results);
    private static string TableDelegateName(Module module, Signature signature, LoweringPlan lowering)
    {
        if (UsesBuiltinDelegate(signature))
        {
            var parameters = signature.Parameters.Select(type => TypeName(type, lowering)).ToArray();
            if (signature.Results.Length == 0)
                return parameters.Length == 0 ? "global::System.Action" : $"global::System.Action<{string.Join(", ", parameters)}>";
            return $"global::System.Func<{string.Join(", ", parameters.Concat(new[] { ResultType(signature.Results, lowering) }))}>";
        }
        for (int i = 0; i < module.Types.Count; i++)
            if (SameSignature(module.Types[i], signature)) return $"__wasm_TableCall{i}";
        throw new WasmException("Missing function signature for table delegate.");
    }
    private static bool UsesBuiltinDelegate(Signature signature) => signature.Parameters.Length <= 16 &&
        !signature.Parameters.Concat(signature.Results).Any(type => type is ValueType.F32 or ValueType.F64 or ValueType.V128);
    private static string ReferenceExpression(Module module, ReferenceValue reference, LoweringPlan lowering)
    {
        if (!reference.FunctionIndex.HasValue) return "null";
        int index = reference.FunctionIndex.Value;
        return $"new {TableDelegateName(module, module.FunctionSignature(index), lowering)}(__wasm_F{index})";
    }
    private static string NullableInt(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null";
    private static string WasmTypeName(ValueType type) => $"global::Wasm2Cs.WasmValueType.{type switch
    {
        ValueType.V128 => "V128", ValueType.I32 => "I32", ValueType.I64 => "I64", ValueType.F32 => "F32", ValueType.F64 => "F64",
        ValueType.FuncRef => "FuncRef", ValueType.ExternRef => "ExternRef",
        _ => throw new WasmException($"Unsupported global type {type}.")
    }}";
    private static string TagParameterTypes(Signature signature) =>
        $"new global::Wasm2Cs.WasmValueType[] {{ {string.Join(", ", signature.Parameters.Select(WasmTypeName))} }}";
    private static string GlobalExpression(Module module, int index) => module.Globals[index].Imported ?
        $"__wasm_GetG{index}()" : $"__wasm_G{index}";
    private static string GlobalSet(Module module, int index, string value) => module.Globals[index].Imported ?
        $"__wasm_SetG{index}({value});" : $"__wasm_G{index} = {value};";
    private static string GlobalAccessor(Module module, int index, LoweringPlan lowering)
    {
        var type = module.Globals[index].Type;
        string getter, setter;
        if (type == ValueType.I32)
        {
            getter = $"unchecked((int)(uint)__wasm_import_global{index}.Bits)";
            setter = $"__wasm_import_global{index}.Bits = unchecked((ulong)(uint)value);";
        }
        else if (type == ValueType.I64)
        {
            getter = $"unchecked((long)__wasm_import_global{index}.Bits)";
            setter = $"__wasm_import_global{index}.Bits = unchecked((ulong)value);";
        }
        else if (type == ValueType.F32)
        {
            getter = $"__wasm_F32(unchecked((int)(uint)__wasm_import_global{index}.Bits))";
            setter = $"__wasm_import_global{index}.Bits = unchecked((ulong)(uint)__wasm_Bits32(value));";
        }
        else if (type == ValueType.F64)
        {
            getter = $"__wasm_F64(unchecked((long)__wasm_import_global{index}.Bits))";
            setter = $"__wasm_import_global{index}.Bits = unchecked((ulong)__wasm_Bits64(value));";
        }
        else
        {
            getter = type == ValueType.FuncRef
                ? $"({TypeName(type, lowering)})__wasm_import_global{index}.ReferenceValue"
                : $"__wasm_import_global{index}.ReferenceValue";
            setter = $"__wasm_import_global{index}.ReferenceValue = value;";
        }
        return $"    private {TypeName(type, lowering)} __wasm_GetG{index}() => {getter};\n" +
            $"    private void __wasm_SetG{index}({TypeName(type, lowering)} value) {{ {setter} }}\n";
    }
    private static string BoundaryTypeName(ValueType type, bool bits, LoweringPlan lowering) => type == ValueType.F32 ? (bits ? "int" : "float") :
        type == ValueType.F64 ? (bits ? "long" : "double") : TypeName(type, lowering);
    private static string BoundaryResultType(ValueType[] types, bool bits, LoweringPlan lowering) => types.Length == 0 ? "void" :
        types.Length == 1 ? BoundaryTypeName(types[0],bits,lowering) : "("+string.Join(", ",types.Select(t=>BoundaryTypeName(t,bits,lowering)))+")";
    private static string BoundaryParameters(ValueType[] types, bool bits, LoweringPlan lowering) => string.Join(", ",types.Select((t,p)=>$"{BoundaryTypeName(t,bits,lowering)} v{p}"));
    private static string FromBoundary(string value, ValueType type, bool bits) => type == ValueType.F32 ?
        (bits ? $"__wasm_F32({value})" : $"__wasm_FromF32({value})") : type == ValueType.F64 ?
        (bits ? $"__wasm_F64({value})" : $"__wasm_FromF64({value})") : value;
    private static string ToBoundary(string value, ValueType type, bool bits) => type == ValueType.F32 ?
        (bits ? $"__wasm_Bits32({value})" : $"({value}).Value") : type == ValueType.F64 ?
        (bits ? $"__wasm_Bits64({value})" : $"({value}).Value") : value;
    private static string ReturnBoundary(string call, ValueType[] types, bool from, bool bits) =>
        ReturnConverted(call, types, (value,type) => from ? FromBoundary(value,type,bits) : ToBoundary(value,type,bits));
    private static string ReturnConverted(string call, ValueType[] types, Func<string,ValueType,string> convert)
    {
        if (types.Length == 0) return call+";";
        if (types.Length == 1) return "return "+convert(call,types[0])+";";
        return "var __wasm_result = "+call+"; return ("+string.Join(", ",types.Select((t,i)=>convert($"__wasm_result.Item{i+1}",t)))+");";
    }
    private static string ConstantExpression(ConstantValue value, Module? module = null)
    {
        if (value.GlobalIndex.HasValue)
        {
            if (module == null) throw new WasmException("Global constant expression requires a module.");
            return GlobalExpression(module, value.GlobalIndex.Value);
        }
        return value.Type switch
        {
            ValueType.I32 => unchecked((int)value.Bits).ToString(CultureInfo.InvariantCulture),
            ValueType.I64 => $"unchecked((long)0x{value.Bits.ToString("x16", CultureInfo.InvariantCulture)}UL)",
            ValueType.F32 => $"__wasm_F32(unchecked((int)0x{value.Bits.ToString("x8", CultureInfo.InvariantCulture)}U))",
            ValueType.F64 => $"__wasm_F64(unchecked((long)0x{value.Bits.ToString("x16", CultureInfo.InvariantCulture)}UL))",
            _ => throw new WasmException($"Unsupported constant type {value.Type}.")
        };
    }
    private static string TypeName(ValueType type, LoweringPlan? lowering = null) => type switch
    {
        ValueType.V128 when lowering != null => lowering.VectorTypeName,
        ValueType.I32 => "int", ValueType.I64 => "long", ValueType.F32 => "__wasm_Float32", ValueType.F64 => "__wasm_Float64",
        ValueType.FuncRef => "global::System.Delegate", ValueType.ExternRef => "object",
        _ => throw new WasmException($"Unsupported generated value type {type}.")
    };
    private static string ResultType(ValueType[] types, LoweringPlan lowering) => types.Length == 0 ? "void" :
        types.Length == 1 ? TypeName(types[0], lowering) : "(" + string.Join(", ", types.Select(type => TypeName(type, lowering))) + ")";
    private static string ResultExpression(Value[] values) => values.Length == 0 ? "" :
        values.Length == 1 ? values[0].Name : "(" + string.Join(", ", values.Select(v => v.Name)) + ")";
    private sealed record Value(string Name, ValueType? Type);
    private sealed class Control(byte opcode, List<Value> values, Value[] parameters, Value[] results, int label)
    {
        public byte Opcode = opcode;
        public List<Value> Values = values;
        public Value[] Parameters = parameters, Results = results;
        public Value[] LabelValues => Opcode == 0x03 ? Parameters : Results;
        public string Start = $"L{label}_start", End = $"L{label}_end", Else = $"L{label}_else";
        public string ExceptionName = $"__wasm_exception_{label}";
        public bool ElseSeen, CatchSeen, CatchAllSeen;
        public string? CaughtException;
    }

    private static IEnumerable<string> EmitBodyChunks(Module module, Function function, LoweringPlan lowering,
        CancellationToken cancellationToken, TempLayout temps, string[] localNames)
    {
        var code = new StringBuilder();
        var stack = new List<Value>();
        int label = 0;
        string Temp(string type)
        {
            if (!temps.ByType.TryGetValue(type, out var slot)) throw new WasmException($"Missing temporary type '{type}'.");
            return $"{slot.Name}[{slot.Count++}]";
        }
        Value[] Temps(ValueType[] types) => types.Select(t => new Value(Temp(TypeName(t, lowering)), t)).ToArray();
        var controls = new List<Control> { new Control(0xff, new List<Value>(), Array.Empty<Value>(), Temps(function.Signature.Results), label++) };
        void Line(string text) => code.Append("        ").Append(text).Append('\n');
        string LocalName(int index) => localNames[index];
        Value Pop(ValueType? expected = null)
        {
            // Only the validator can introduce bottom. Give it the consuming instruction's type.
            var value = new Value("default", null);
            if (stack.Count > controls[controls.Count-1].Values.Count)
            {
                value = stack[stack.Count-1]; stack.RemoveAt(stack.Count-1);
            }
            return value.Type.HasValue ? value : new Value($"default({TypeName(expected ?? ValueType.I32, lowering)})", expected);
        }
        Value[] PopTypes(ValueType[] types)
        {
            var values = new Value[types.Length];
            for (int i = types.Length-1; i >= 0; i--) values[i] = Pop(types[i]);
            return values;
        }
        Value[] PopValues(Value[] destinations) => PopTypes(destinations.Select(v => v.Type!.Value).ToArray());
        void Push(string expression, ValueType type = ValueType.I32)
        {
            var value = new Value(Temp(TypeName(type, lowering)), type);
            Line($"{value.Name} = {expression};"); stack.Add(value);
        }
        void Restore(Control frame) { stack = new List<Value>(frame.Values); }
        void Transfer(Value[] destinations, Value[] values)
        {
            // Snapshot all sources before assigning destinations: loop backedges can permute parameters.
            var snapshots = Temps(destinations.Select(v => v.Type!.Value).ToArray());
            for (int i = 0; i < values.Length; i++) Line($"{snapshots[i].Name} = {(values[i].Type.HasValue ? values[i].Name : $"default({TypeName(destinations[i].Type!.Value, lowering)})")};");
            for (int i = 0; i < values.Length; i++) Line($"{destinations[i].Name} = {snapshots[i].Name};");
        }
        void Branch(Control target, Value[] values)
        {
            Transfer(target.LabelValues, values);
            Line($"goto {(target.Opcode == 0x03 ? target.Start : target.End)};");
        }
        foreach (var instruction in function.Instructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonicalInstruction = CanonicalLowering.LowerInstruction(instruction);
            var current = controls[controls.Count-1];
            Control Target(int depth) => controls[controls.Count-1-depth];
            string PopI32() => Pop(ValueType.I32).Name;

            if (canonicalInstruction.Kind == CanonicalInstructionKind.VectorConstant && canonicalInstruction.VectorConstant is not null)
            {
                if (!lowering.TryLowerVectorConstant(canonicalInstruction.VectorConstant.Bytes, out var vectorConstantLowering) ||
                    vectorConstantLowering is null)
                    throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower v128.const.");
                Push(vectorConstantLowering.Expression, ValueType.V128);
                if (code.Length >= FunctionChunkCharacters) { yield return code.ToString(); code.Clear(); }
                continue;
            }
            if (canonicalInstruction.Kind == CanonicalInstructionKind.VectorOperation && canonicalInstruction.VectorOperation.HasValue)
            {
                var vectorOp = canonicalInstruction.VectorOperation.Value;
                if (!SimdOperations.TryDescribe(instruction.Operand, out _, out var simdKind, out _, out _))
                    throw new WasmException($"Unknown SIMD operand {instruction.Operand}.");
                VectorLowering? vectorResult = null;
                switch (simdKind)
                {
                    case SimdOpKind.Binary:
                    {
                        string vectorRight = Pop(ValueType.V128).Name;
                        string vectorLeft = Pop(ValueType.V128).Name;
                        if (!lowering.TryLowerVector(vectorOp, vectorLeft, vectorRight, out vectorResult) || vectorResult is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower vector operation {instruction.Operand}.");
                        Push(vectorResult.Expression, ValueType.V128);
                        break;
                    }
                    case SimdOpKind.Unary:
                    {
                        string vectorValue = Pop(ValueType.V128).Name;
                        if (!lowering.TryLowerVectorUnary(vectorOp, vectorValue, out vectorResult) || vectorResult is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower vector operation {instruction.Operand}.");
                        Push(vectorResult.Expression, ValueType.V128);
                        break;
                    }
                    case SimdOpKind.Ternary:
                    {
                        string c = Pop(ValueType.V128).Name;
                        string b = Pop(ValueType.V128).Name;
                        string a = Pop(ValueType.V128).Name;
                        if (!lowering.TryLowerVectorTernary(vectorOp, a, b, c, out vectorResult) || vectorResult is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower vector operation {instruction.Operand}.");
                        Push(vectorResult.Expression, ValueType.V128);
                        break;
                    }
                    case SimdOpKind.SplatI32:
                    {
                        string scalar = Pop(ValueType.I32).Name;
                        if (!lowering.TryLowerVectorSplat(vectorOp, scalar, out vectorResult) || vectorResult is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower vector operation {instruction.Operand}.");
                        Push(vectorResult.Expression, ValueType.V128);
                        break;
                    }
                    case SimdOpKind.SplatF32:
                    {
                        string scalar = Pop(ValueType.F32).Name;
                        if (!lowering.TryLowerVectorSplat(vectorOp, scalar, out vectorResult) || vectorResult is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower vector operation {instruction.Operand}.");
                        Push(vectorResult.Expression, ValueType.V128);
                        break;
                    }
                    case SimdOpKind.ExtractI32:
                    {
                        string vectorValue = Pop(ValueType.V128).Name;
                        if (!lowering.TryLowerVectorExtract(vectorOp, vectorValue, (int)instruction.Immediate, out var extract) || extract is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower vector operation {instruction.Operand}.");
                        Push(extract, ValueType.I32);
                        break;
                    }
                    case SimdOpKind.Shuffle:
                    {
                        string vectorRight = Pop(ValueType.V128).Name;
                        string vectorLeft = Pop(ValueType.V128).Name;
                        byte[] lanes = instruction.VectorConstant ?? Array.Empty<byte>();
                        if (!lowering.TryLowerVectorShuffle(vectorLeft, vectorRight, lanes, out vectorResult) || vectorResult is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower vector operation {instruction.Operand}.");
                        Push(vectorResult.Expression, ValueType.V128);
                        break;
                    }
                    default:
                        throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower vector operation {instruction.Operand}.");
                }
                if (code.Length >= FunctionChunkCharacters) { yield return code.ToString(); code.Clear(); }
                continue;
            }
            if (canonicalInstruction.Kind == CanonicalInstructionKind.ScalarOperation && canonicalInstruction.Operation is not null)
            {
                var operation = canonicalInstruction.Operation;
                string right = operation.Inputs.Length == 2 ? Pop(operation.Inputs[1]).Name : "";
                string left = Pop(operation.Inputs[0]).Name;
                Push(lowering.Expression(operation, left, right), operation.Result);
                if (code.Length >= FunctionChunkCharacters) { yield return code.ToString(); code.Clear(); }
                continue;
            }
            switch (instruction.Opcode)
            {
                case 0x02: case 0x03: case 0x04: case 0x06:
                    string condition = instruction.Opcode == 0x04 ? PopI32() : "";
                    var block = instruction.BlockType!;
                    var inputs = PopTypes(block.Parameters);
                    var frame = new Control(instruction.Opcode, new List<Value>(stack), Temps(block.Parameters), Temps(block.Results), label++);
                    Transfer(frame.Parameters, inputs);
                    controls.Add(frame);
                    stack.AddRange(frame.Parameters);
                    if (instruction.Opcode == 0x03) Line(frame.Start + ": ;");
                    if (instruction.Opcode == 0x04) Line($"if ({condition} == 0) goto {frame.Else};");
                    if (instruction.Opcode == 0x06) { Line("try"); Line("{"); }
                    break;
                case 0x07:
                    Transfer(current.Results, PopValues(current.Results));
                    Line("}");
                    current.CatchSeen = true;
                    current.CaughtException = current.ExceptionName;
                    Line($"catch (global::Wasm2Cs.WasmThrownException {current.CaughtException}) when (global::System.Object.ReferenceEquals({current.CaughtException}.Tag, __wasm_tag{instruction.Operand}))");
                    Line("{");
                    Restore(current);
                    var catchValues = Temps(module.Tags[instruction.Operand].Signature.Parameters);
                    for (int i = 0; i < catchValues.Length; i++)
                        Line($"{catchValues[i].Name} = ({TypeName(catchValues[i].Type!.Value, lowering)}){current.CaughtException}.Payload[{i}];");
                    stack.AddRange(catchValues);
                    break;
                case 0x19:
                    Transfer(current.Results, PopValues(current.Results));
                    Line("}");
                    current.CatchSeen = true; current.CatchAllSeen = true;
                    current.CaughtException = current.ExceptionName;
                    Line($"catch (global::Wasm2Cs.WasmThrownException {current.CaughtException})");
                    Line("{");
                    Restore(current);
                    break;
                case 0x05:
                    Transfer(current.Results, PopValues(current.Results));
                    Line($"goto {current.End};"); Line(current.Else + ": ;");
                    current.ElseSeen = true; Restore(current); stack.AddRange(current.Parameters);
                    break;
                case 0x0b:
                    Transfer(current.Results, PopValues(current.Results));
                    if (current.Opcode == 0x04 && !current.ElseSeen)
                    {
                        Line($"goto {current.End};"); Line(current.Else + ": ;");
                        Transfer(current.Results, current.Parameters);
                    }
                    if (current.Opcode == 0x06) Line("}");
                    Line(current.End + ": ;");
                    Restore(current);
                    controls.RemoveAt(controls.Count-1);
                    if (current.Opcode == 0xff) Line("return" + (current.Results.Length == 0 ? "" : " " + ResultExpression(current.Results)) + ";");
                    else stack.AddRange(current.Results);
                    break;
                case 0x00: Line("throw new TrapException(TrapKind.Unreachable);"); Restore(current); break;
                case 0x0c: case 0x0d:
                    string branchCondition = instruction.Opcode == 0x0d ? PopI32() : "";
                    var target = Target(instruction.Operand);
                    var values = PopValues(target.LabelValues);
                    if (instruction.Opcode == 0x0d)
                    {
                        Line($"if ({branchCondition} != 0) {{"); Branch(target, values); Line("}");
                        stack.AddRange(values);
                    }
                    else { Branch(target, values); Restore(current); }
                    break;
                case 0x0e:
                    string selector = PopI32();
                    var targets = instruction.Targets!;
                    var branchValues = new Value[Target(targets[0]).LabelValues.Length];
                    for (int p = branchValues.Length-1; p >= 0; p--) branchValues[p] = Pop();
                    Line($"switch (unchecked((uint){selector})) {{");
                    for (int i = 0; i < targets.Length; i++)
                    {
                        Line(i == targets.Length-1 ? "default:" : $"case {i}u:");
                        Branch(Target(targets[i]), branchValues);
                    }
                    Line("}"); Restore(current); break;
                case 0x0f:
                    var returned = PopTypes(function.Signature.Results);
                    Line("return" + (returned.Length == 0 ? "" : " " + ResultExpression(returned)) + ";");
                    Restore(current);
                    break;
                case 0x08:
                    var thrownTag = module.Tags[instruction.Operand];
                    var payload = PopTypes(thrownTag.Signature.Parameters);
                    Line($"throw new global::Wasm2Cs.WasmThrownException(__wasm_tag{instruction.Operand}, new object[] {{ {string.Join(", ", payload.Select(v => v.Name))} }});");
                    Restore(current);
                    break;
                case 0x09:
                    var rethrowTarget = Target(instruction.Operand);
                    Line($"throw {rethrowTarget.CaughtException};");
                    Restore(current);
                    break;
                case 0x18:
                    Transfer(current.Results, PopValues(current.Results));
                    Line("}");
                    Line($"catch (global::Wasm2Cs.WasmThrownException {current.ExceptionName})");
                    Line("{");
                    Line("throw;");
                    Line("}");
                    Line(current.End + ": ;");
                    Restore(current);
                    controls.RemoveAt(controls.Count-1);
                    stack.AddRange(current.Results);
                    break;
                case 0x10:
                    var signature = module.FunctionSignature(instruction.Operand);
                    var arguments = PopTypes(signature.Parameters);
                    string call = $"__wasm_F{instruction.Operand}({string.Join(", ", arguments.Select(v => v.Name))})";
                    if (signature.Results.Length == 0) Line(call + ";");
                    else if (signature.Results.Length == 1) Push(call, signature.Results[0]);
                    else
                    {
                        string tuple = Temp(ResultType(signature.Results, lowering));
                        Line($"{tuple} = {call};");
                        for (int i = 0; i < signature.Results.Length; i++)
                            stack.Add(new Value($"{tuple}.Item{i+1}", signature.Results[i]));
                    }
                    break;
                case 0x11:
                    var indirectSignature = module.Types[instruction.Operand];
                    string indirectIndex = PopI32();
                    var indirectArguments = PopTypes(indirectSignature.Parameters);
                    string indirectDelegateType = TableDelegateName(module, indirectSignature, lowering);
                    string indirectValue = Temp("object");
                    string indirectTarget = Temp(indirectDelegateType);
                    Line($"{indirectValue} = __wasm_TableGet(__wasm_table{instruction.Immediate}, {indirectIndex});");
                    Line($"if ({indirectValue} == null) throw new TrapException(TrapKind.IndirectCallNull);");
                    Line($"{indirectTarget} = {indirectValue} as {indirectDelegateType};");
                    Line($"if ({indirectTarget} == null) throw new TrapException(TrapKind.IndirectCallTypeMismatch);");
                    string indirectCall = $"{indirectTarget}({string.Join(", ", indirectArguments.Select(v => v.Name))})";
                    if (indirectSignature.Results.Length == 0) Line(indirectCall + ";");
                    else if (indirectSignature.Results.Length == 1) Push(indirectCall, indirectSignature.Results[0]);
                    else
                    {
                        string indirectTuple = Temp(ResultType(indirectSignature.Results, lowering));
                        Line($"{indirectTuple} = {indirectCall};");
                        for (int i = 0; i < indirectSignature.Results.Length; i++)
                            stack.Add(new Value($"{indirectTuple}.Item{i+1}", indirectSignature.Results[i]));
                    }
                    break;
                case 0x1a: Pop(); break;
                case 0x1b: case 0x1c:
                    string selected = PopI32();
                    var selectedType = instruction.ResultTypes[0];
                    var b = Pop(selectedType); var a = Pop(selectedType);
                    if (selectedType.HasValue) Push($"{selected} != 0 ? {a.Name} : {b.Name}", selectedType.Value);
                    else stack.Add(new Value("default", null));
                    break;
                case 0x20: Push(LocalName(instruction.Operand), function.Locals[instruction.Operand]); break;
                case 0x21: case 0x22:
                    var localValue = Pop(function.Locals[instruction.Operand]);
                    Line($"{LocalName(instruction.Operand)} = {localValue.Name};");
                    if (instruction.Opcode == 0x22) stack.Add(localValue);
                    break;
                case 0x41: case 0x42: case 0x43: case 0x44: Push(ConstantExpression(instruction.Constant!), instruction.Constant!.Type); break;
                case 0x23: Push(GlobalExpression(module, instruction.Operand), module.Globals[instruction.Operand].Type); break;
                case 0x24:
                    var globalValue = Pop(module.Globals[instruction.Operand].Type).Name;
                    Line(GlobalSet(module, instruction.Operand, globalValue));
                    break;
                case 0x25:
                    var getTable = module.Tables[instruction.Operand];
                    string tableIndex = PopI32();
                    Push($"({TypeName(getTable.ElementType, lowering)})__wasm_TableGet(__wasm_table{instruction.Operand}, {tableIndex})", getTable.ElementType);
                    break;
                case 0x26:
                    var setTable = module.Tables[instruction.Operand];
                    string tableValue = Pop(setTable.ElementType).Name;
                    string setIndex = PopI32();
                    Line($"__wasm_TableSet(__wasm_table{instruction.Operand}, {setIndex}, {tableValue});");
                    break;
                case 0x2a: case 0x2b:
                case 0x28: case 0x2c: case 0x2d: case 0x2e: case 0x2f:
                case 0x29: case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:
                    var loadType = MemoryOperations.Type(instruction.Opcode);
                    string load = loadType == ValueType.I64 || loadType == ValueType.F64 ? "__wasm_Load64" : "__wasm_Load";
                    string loaded = $"{load}({PopI32()}, {instruction.Immediate}u, {MemoryOperations.Width(instruction.Opcode)}, {(MemoryOperations.IsSigned(instruction.Opcode) ? "true" : "false")})";
                    if (loadType == ValueType.F32) loaded = $"__wasm_F32({loaded})";
                    if (loadType == ValueType.F64) loaded = $"__wasm_F64({loaded})";
                    Push(loaded, loadType); break;
                case 0x38: case 0x39:
                case 0x36: case 0x3a: case 0x3b:
                case 0x37: case 0x3c: case 0x3d: case 0x3e:
                    var storeType = MemoryOperations.Type(instruction.Opcode);
                    string store = storeType == ValueType.I64 || storeType == ValueType.F64 ? "__wasm_Store64" : "__wasm_Store";
                    string stored = Pop(storeType).Name, address = PopI32();
                    if (storeType == ValueType.F32) stored = $"__wasm_Bits32({stored})";
                    if (storeType == ValueType.F64) stored = $"__wasm_Bits64({stored})";
                    Line($"{store}({address}, {stored}, {instruction.Immediate}u, {MemoryOperations.Width(instruction.Opcode)});"); break;
                case 0x3f: Push("__wasm_memory.CurrentPages"); break;
                case 0x40: Push($"__wasm_Grow({PopI32()})"); break;
                case 0xfd:
                {
                    if (!SimdOperations.TryDescribe(instruction.Operand, out _, out var memSimdKind, out _, out _))
                        throw new WasmException($"Unsupported SIMD opcode 0xfd/{instruction.Operand}.");
                    if (memSimdKind is SimdOpKind.Load or SimdOpKind.Load32Zero)
                    {
                        string v128Address = PopI32();
                        if (!lowering.TryLowerVectorLoad(memSimdKind == SimdOpKind.Load32Zero, v128Address, instruction.Immediate, out var v128Loaded) || v128Loaded is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower SIMD load.");
                        Push(v128Loaded.Expression, ValueType.V128);
                    }
                    else if (memSimdKind == SimdOpKind.Store)
                    {
                        string v128Stored = Pop(ValueType.V128).Name;
                        string v128Address = PopI32();
                        if (!lowering.TryLowerVectorStore(v128Address, v128Stored, instruction.Immediate, out var v128Store) || v128Store is null)
                            throw new WasmException($"Target profile '{WasmTargetProfiles.Name(lowering.Profile)}' cannot lower SIMD store.");
                        Line(v128Store + ";");
                    }
                    else
                        throw new WasmException($"SIMD opcode 0xfd/{instruction.Operand} was not lowered.");
                    break;
                }
                case 0xfc when instruction.Operand == 8:
                    string initLength = PopI32();
                    string initSource = PopI32();
                    string initDestination = PopI32();
                    Line($"__wasm_Init(__wasm_D{instruction.Immediate}, {initDestination}, {initSource}, {initLength});");
                    break;
                case 0xfc when instruction.Operand == 9:
                    Line($"__wasm_D{instruction.Immediate} = null;");
                    break;
                case 0xfc when instruction.Operand == 10:
                    string copyLength = PopI32();
                    string copySource = PopI32();
                    string copyDestination = PopI32();
                    Line($"__wasm_Copy({copyDestination}, {copySource}, {copyLength});");
                    break;
                case 0xfc when instruction.Operand == 11:
                    string fillLength = PopI32();
                    string fillValue = PopI32();
                    string fillDestination = PopI32();
                    Line($"__wasm_Fill({fillDestination}, {fillValue}, {fillLength});");
                    break;
                case 0xfc when instruction.Operand == 12:
                    string tableInitLength = PopI32();
                    string tableInitSource = PopI32();
                    string tableInitDestination = PopI32();
                    Line($"__wasm_TableInit(__wasm_table{instruction.Secondary}, __wasm_E{instruction.Immediate}, {tableInitDestination}, {tableInitSource}, {tableInitLength});");
                    break;
                case 0xfc when instruction.Operand == 13:
                    Line($"__wasm_E{instruction.Immediate} = null;");
                    break;
                case 0xfc when instruction.Operand == 14:
                    string tableCopyLength = PopI32();
                    string tableCopySource = PopI32();
                    string tableCopyDestination = PopI32();
                    Line($"__wasm_TableCopy(__wasm_table{instruction.Immediate}, __wasm_table{instruction.Secondary}, {tableCopyDestination}, {tableCopySource}, {tableCopyLength});");
                    break;
                case 0xfc when instruction.Operand == 15:
                    string tableGrowValue = Pop(module.Tables[(int)instruction.Secondary].ElementType).Name;
                    string tableGrowDelta = PopI32();
                    Push($"__wasm_TableGrow(__wasm_table{instruction.Secondary}, {tableGrowDelta}, {tableGrowValue})");
                    break;
                case 0xfc when instruction.Operand == 16:
                    Push($"__wasm_table{instruction.Secondary}.CurrentSize");
                    break;
                case 0xfc when instruction.Operand == 17:
                    string tableFillLength = PopI32();
                    string tableFillValue = Pop(module.Tables[(int)instruction.Secondary].ElementType).Name;
                    string tableFillDestination = PopI32();
                    Line($"__wasm_TableFill(__wasm_table{instruction.Secondary}, {tableFillDestination}, {tableFillValue}, {tableFillLength});");
                    break;
                case 0xd0:
                    Push("null", instruction.Reference!.Type);
                    break;
                case 0xd1:
                    var reference = Pop();
                    string referenceValue = reference.Type.HasValue ? reference.Name : "null";
                    Push($"({referenceValue} == null ? 1 : 0)");
                    break;
                case 0xd2:
                    Push(ReferenceExpression(module, instruction.Reference!, lowering), ValueType.FuncRef);
                    break;
                default:
                    break;
            }
            if (code.Length >= FunctionChunkCharacters) { yield return code.ToString(); code.Clear(); }
        }
        if (code.Length != 0) yield return code.ToString();
    }
}
