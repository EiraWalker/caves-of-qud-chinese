using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types) {
        yield return type;
        foreach (var child in AllTypes(type.NestedTypes)) yield return child;
    }
}
static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
static string Operand(Instruction ins, MethodDefinition method, bool omitStrings)
{
    var list = method.Body.Instructions;
    return ins.Operand switch {
        null => "",
        string str => omitStrings ? "<str>" : str,
        Instruction target => "@" + list.IndexOf(target),
        Instruction[] targets => string.Join(",", targets.Select(t => "@" + list.IndexOf(t))),
        MemberReference member => member.FullName,
        VariableDefinition variable => "$" + variable.Index + ":" + variable.VariableType.FullName,
        ParameterDefinition parameter => "#" + parameter.Index + ":" + parameter.ParameterType.FullName,
        _ => Convert.ToString(ins.Operand, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };
}
static string Shape(MethodDefinition method, bool omitStrings)
{
    if (!method.HasBody) return "";
    var b = method.Body;
    var parts = new List<string> { method.FullName, b.InitLocals.ToString(), b.MaxStackSize.ToString(), string.Join(";", b.Variables.Select(v => v.VariableType.FullName)) };
    parts.AddRange(b.Instructions.Select(i => i.OpCode.Code + " " + Operand(i, method, omitStrings)));
    parts.AddRange(b.ExceptionHandlers.Select(e => $"EH:{e.HandlerType}:{e.CatchType?.FullName}:{b.Instructions.IndexOf(e.TryStart)}:{(e.TryEnd == null ? -1 : b.Instructions.IndexOf(e.TryEnd))}:{b.Instructions.IndexOf(e.HandlerStart)}:{(e.HandlerEnd == null ? -1 : b.Instructions.IndexOf(e.HandlerEnd))}:{(e.FilterStart == null ? -1 : b.Instructions.IndexOf(e.FilterStart))}"));
    return Hash(string.Join("\n", parts));
}
static object Snapshot(AssemblyDefinition asm)
{
    return new {
        version = asm.Name.Version.ToString(),
        assembly = asm.Name.FullName,
        references = asm.MainModule.AssemblyReferences.Select(a => a.FullName).ToArray(),
        resources = asm.MainModule.Resources.Select(r => r is EmbeddedResource er ? r.Name + ":" + Hash(Convert.ToBase64String(er.GetResourceData())) : r.Name).ToArray(),
        types = AllTypes(asm.MainModule.Types).Select(t => new {
            name = t.FullName, attributes = (int)t.Attributes, baseType = t.BaseType?.FullName,
            interfaces = t.Interfaces.Select(i => i.InterfaceType.FullName).ToArray(),
            fields = t.Fields.Select(f => new {name = f.FullName, attributes=(int)f.Attributes, constant=f.HasConstant ? f.Constant?.ToString() : null, initialValue=Convert.ToBase64String(f.InitialValue ?? [])}).ToArray(),
            properties = t.Properties.Select(p => p.FullName).ToArray(), events = t.Events.Select(e => e.FullName).ToArray(),
            methods = t.Methods.Select(m => new {
                name = m.FullName, attributes=(int)m.Attributes, impl=(int)m.ImplAttributes,
                shape = Shape(m, true), fullHash=Shape(m, false),
                strings = m.HasBody ? m.Body.Instructions.Select((i,n) => (i,n)).Where(x => x.i.OpCode.Code == Code.Ldstr).Select(x => new {
                    index=x.n, value=(string)x.i.Operand,
                    context=m.Body.Instructions.Skip(Math.Max(0,x.n-3)).Take(9).Select(i => i.OpCode.Code + " " + Operand(i,m,true)).ToArray(),
                    after=m.Body.Instructions.Skip(x.n+1).Take(12).Select(i => i.OpCode.Code + " " + Operand(i,m,true)).ToArray()
                }).ToArray() : []
            }).ToArray()
        }).ToArray()
    };
}
var json = new JsonSerializerOptions {WriteIndented = false};
var command = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
if (args.Length > 4) resolver.AddSearchDirectory(args[4]);
using var assembly = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters {AssemblyResolver=resolver, InMemory=true, ReadSymbols=false});
if (command == "dump") {
    File.WriteAllText(args[2], JsonSerializer.Serialize(Snapshot(assembly), json));
    Console.WriteLine($"Dumped {assembly.Name.FullName} to {args[2]}");
} else if (command == "patch") {
    using var doc = JsonDocument.Parse(File.ReadAllText(args[2]));
    var methods = AllTypes(assembly.MainModule.Types).SelectMany(t => t.Methods).GroupBy(m => m.FullName).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
    var count=0;
    foreach (var item in doc.RootElement.EnumerateArray()) {
        var key=item.GetProperty("method").GetString()!;
        var method=methods[key];
        if (Shape(method,true) != item.GetProperty("shape").GetString()) throw new Exception("Method shape changed: " + key);
        var ins=method.Body.Instructions[item.GetProperty("index").GetInt32()];
        if (ins.OpCode.Code != Code.Ldstr || (string)ins.Operand != item.GetProperty("old").GetString()) throw new Exception("String precondition failed: " + key);
        ins.Operand=item.GetProperty("new").GetString()!;
        count++;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    assembly.Write(args[3], new WriterParameters {WriteSymbols=false});
    Console.WriteLine($"Patched {count} ldstr operands; preserved version {assembly.Name.Version}; output {args[3]}");
} else throw new ArgumentException("Unknown command");
