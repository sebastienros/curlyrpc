using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CurlyRpc.SourceGenerator;

/// <summary>
/// Generates typed, AOT-safe client proxies for interfaces annotated with
/// <c>[CurlyRpc.JsonRpcProxy]</c>. For each such interface the generator emits a sealed proxy class
/// and a <c>Create{Interface}Proxy</c> extension method on <c>CurlyRpc.JsonRpc</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class JsonRpcProxyGenerator : IIncrementalGenerator
{
    private const string ProxyAttributeName = "CurlyRpc.JsonRpcProxyAttribute";
    private const string MethodAttributeName = "CurlyRpc.JsonRpcMethodAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ProxyModel?> models = context.SyntaxProvider.ForAttributeWithMetadataName(
            ProxyAttributeName,
            predicate: static (node, _) => node is InterfaceDeclarationSyntax,
            transform: static (ctx, _) => Build((INamedTypeSymbol)ctx.TargetSymbol));

        context.RegisterSourceOutput(
            models.Where(static m => m is not null),
            static (spc, model) => Emit(spc, model!));
    }

    private static ProxyModel? Build(INamedTypeSymbol interfaceSymbol)
    {
        var methods = new List<MethodModel>();

        foreach (INamedTypeSymbol type in EnumerateInterface(interfaceSymbol))
        {
            foreach (ISymbol member in type.GetMembers())
            {
                if (member is not IMethodSymbol method || method.MethodKind != MethodKind.Ordinary)
                {
                    continue;
                }

                methods.Add(BuildMethod(method));
            }
        }

        string ns = interfaceSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : interfaceSymbol.ContainingNamespace.ToDisplayString();

        return new ProxyModel(
            ns,
            interfaceSymbol.Name,
            interfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            methods);
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateInterface(INamedTypeSymbol interfaceSymbol)
    {
        yield return interfaceSymbol;
        foreach (INamedTypeSymbol inherited in interfaceSymbol.AllInterfaces)
        {
            yield return inherited;
        }
    }

    private static MethodModel BuildMethod(IMethodSymbol method)
    {
        string wireName = method.Name;
        foreach (AttributeData attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == MethodAttributeName
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string explicitName)
            {
                wireName = explicitName;
            }
        }

        var valueParameters = new List<string>();
        string? cancellationToken = null;
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                cancellationToken = Escape(parameter.Name);
            }
            else
            {
                valueParameters.Add(Escape(parameter.Name));
            }
        }

        (ReturnShape shape, string? elementType) = Classify(method.ReturnType);

        return new MethodModel(
            Escape(method.Name),
            wireName,
            method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            shape,
            elementType,
            method.Parameters.Select(p => new ParameterModel(
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Escape(p.Name))).ToImmutableArray(),
            valueParameters.ToImmutableArray(),
            cancellationToken);
    }

    /// <summary>
    /// Prefixes reserved C# keywords with <c>@</c> so interfaces using escaped identifiers
    /// (for example <c>int @params</c> or <c>Task @event()</c>) generate compilable code. Roslyn
    /// reports symbol names without the escape, so it must be reapplied on emit.
    /// </summary>
    private static string Escape(string identifier)
        => SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;

    private static (ReturnShape Shape, string? ElementType) Classify(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol named)
        {
            return (ReturnShape.Unsupported, null);
        }

        string ns = named.ContainingNamespace.ToDisplayString();
        string name = named.Name;

        if (ns == "System.Threading.Tasks")
        {
            if (name == "Task")
            {
                return named.TypeArguments.Length == 1
                    ? (ReturnShape.TaskOfT, named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    : (ReturnShape.Task, null);
            }

            if (name == "ValueTask")
            {
                return named.TypeArguments.Length == 1
                    ? (ReturnShape.ValueTaskOfT, named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    : (ReturnShape.ValueTask, null);
            }
        }

        if (ns == "System.Collections.Generic" && name == "IAsyncEnumerable" && named.TypeArguments.Length == 1)
        {
            return (ReturnShape.AsyncEnumerable, named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        return (ReturnShape.Unsupported, null);
    }

    private static void Emit(SourceProductionContext context, ProxyModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("#pragma warning disable");

        bool hasNamespace = model.Namespace.Length > 0;
        string proxyTypeName = model.InterfaceName + "Proxy";
        string proxyFqn = hasNamespace ? $"global::{model.Namespace}.{proxyTypeName}" : $"global::{proxyTypeName}";

        if (hasNamespace)
        {
            sb.AppendLine($"namespace {model.Namespace}");
            sb.AppendLine("{");
        }

        sb.AppendLine($"    internal sealed class {proxyTypeName} : {model.InterfaceFqn}");
        sb.AppendLine("    {");
        sb.AppendLine("        private readonly global::CurlyRpc.JsonRpc _rpc;");
        sb.AppendLine($"        public {proxyTypeName}(global::CurlyRpc.JsonRpc rpc) {{ _rpc = rpc; }}");

        foreach (MethodModel method in model.Methods)
        {
            EmitMethod(sb, method);
        }

        sb.AppendLine("    }");

        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        sb.AppendLine();
        sb.AppendLine("namespace CurlyRpc");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Generated factory extensions for JSON-RPC client proxies.</summary>");
        sb.AppendLine("    public static partial class JsonRpcProxyExtensions");
        sb.AppendLine("    {");
        sb.AppendLine($"        /// <summary>Creates a client proxy implementing <c>{model.InterfaceName}</c>.</summary>");
        sb.AppendLine($"        public static {model.InterfaceFqn} Create{model.InterfaceName}Proxy(this global::CurlyRpc.JsonRpc rpc)");
        sb.AppendLine($"            => new {proxyFqn}(rpc);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        string hintName = hasNamespace
            ? $"{model.Namespace}.{model.InterfaceName}Proxy.g.cs"
            : $"{model.InterfaceName}Proxy.g.cs";

        context.AddSource(hintName, SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitMethod(StringBuilder sb, MethodModel method)
    {
        string parameterList = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"));
        string args = method.ValueParameterNames.Length == 0
            ? "System.Array.Empty<object>()"
            : "new object[] { " + string.Join(", ", method.ValueParameterNames) + " }";
        string ct = method.CancellationTokenName ?? "default";
        string wire = Quote(method.WireName);

        sb.Append($"        public {method.ReturnFqn} {method.Name}({parameterList}) => ");

        switch (method.Shape)
        {
            case ReturnShape.Task:
                sb.AppendLine($"_rpc.InvokeAsync({wire}, {args}, {ct});");
                break;
            case ReturnShape.TaskOfT:
                sb.AppendLine($"_rpc.InvokeAsync<{method.ElementType}>({wire}, {args}, {ct});");
                break;
            case ReturnShape.ValueTask:
                sb.AppendLine($"new global::System.Threading.Tasks.ValueTask(_rpc.InvokeAsync({wire}, {args}, {ct}));");
                break;
            case ReturnShape.ValueTaskOfT:
                sb.AppendLine($"new global::System.Threading.Tasks.ValueTask<{method.ElementType}>(_rpc.InvokeAsync<{method.ElementType}>({wire}, {args}, {ct}));");
                break;
            case ReturnShape.AsyncEnumerable:
                sb.AppendLine($"_rpc.InvokeAsyncEnumerable<{method.ElementType}>({wire}, {args}, {ct});");
                break;
            default:
                sb.AppendLine($"throw new global::System.NotSupportedException(\"Unsupported JSON-RPC proxy return type on method '{method.Name}'.\");");
                break;
        }
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private enum ReturnShape
    {
        Unsupported,
        Task,
        TaskOfT,
        ValueTask,
        ValueTaskOfT,
        AsyncEnumerable,
    }

    private sealed record ProxyModel(
        string Namespace,
        string InterfaceName,
        string InterfaceFqn,
        List<MethodModel> Methods);

    private sealed record MethodModel(
        string Name,
        string WireName,
        string ReturnFqn,
        ReturnShape Shape,
        string? ElementType,
        ImmutableArray<ParameterModel> Parameters,
        ImmutableArray<string> ValueParameterNames,
        string? CancellationTokenName);

    private sealed record ParameterModel(string Type, string Name);
}
