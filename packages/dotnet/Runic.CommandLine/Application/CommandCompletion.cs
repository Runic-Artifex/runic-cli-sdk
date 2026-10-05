using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Runic.CommandLine;

/// <summary>The catalog candidates and filesystem hint at the current token.</summary>
public sealed class CommandCompletionResult
{
    internal CommandCompletionResult(IEnumerable<string> candidates, CommandPathKind pathKind, string valuePrefix)
    {
        Candidates = Array.AsReadOnly(candidates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        PathKind = pathKind;
        ValuePrefix = valuePrefix;
    }
    /// <summary>Gets candidates, including any inline option prefix.</summary>
    public IReadOnlyList<string> Candidates { get; }
    /// <summary>Gets the filesystem completion kind for the value being entered.</summary>
    public CommandPathKind PathKind { get; }
    /// <summary>Gets the option prefix to retain when completing an inline value.</summary>
    public string ValuePrefix { get; }
}

/// <summary>Queries and generates contextual completions from the parsing catalog.</summary>
public static class CommandCompletion
{
    /// <summary>Queries tokenized arguments excluding the executable. The final token is the current prefix; append an empty token after whitespace.</summary>
    public static CommandCompletionResult Query(CommandCatalog catalog, IReadOnlyList<string> words, string outputOptionName = "--output")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(words);
        _ = new ParseSettings(transportOutputOptionName: outputOptionName);
        var model = new Model(catalog, outputOptionName);
        int node = 0, position = 0, consumed = 0;
        Value? pending = null;
        bool ended = false, positional = false;
        for (int i = 0; i < words.Count - 1; i++)
        {
            string word = words[i];
            if (pending is not null)
            {
                if (!ended && (word == "--" || model.Nodes[node].Options.ContainsKey(word.Split('=', 2)[0]))) pending = null;
                else
                {
                    consumed++;
                    if (consumed >= pending.Maximum) pending = null;
                    continue;
                }
            }
            if (!ended && word == "--") { ended = true; positional = true; continue; }
            string selector = word.Split('=', 2)[0];
            if (!ended && model.Nodes[node].Options.TryGetValue(selector, out Value? option))
            {
                if (option.TargetNode != 0) node = option.TargetNode;
                if (node != 0) positional = true;
                consumed = word.Contains('=') ? 1 : 0;
                if (consumed < option.Maximum) pending = option;
                continue;
            }
            if (!ended && !positional && model.Nodes[node].Children.TryGetValue(word, out int child))
            { node = child; position = 0; continue; }
            if (node == 0 && model.DefaultNode != 0) node = model.DefaultNode;
            position++;
            positional = true;
        }
        string current = words.Count == 0 ? "" : words[^1];
        string prefix = "";
        if (!ended && current.Contains('='))
        {
            string selector = current.Split('=', 2)[0];
            if (model.Nodes[node].Options.TryGetValue(selector, out Value? option))
            { pending = option; prefix = selector + "="; current = current[prefix.Length..]; }
        }
        if (pending is not null && current.StartsWith('-') && prefix.Length == 0) pending = null;
        Value? argument = model.Argument(node, position);
        IEnumerable<string> candidates;
        CommandPathKind kind;
        if (pending is not null) { candidates = pending.Choices; kind = pending.PathKind; }
        else
        {
            candidates = (!ended && !positional ? model.Nodes[node].Children.Keys : Enumerable.Empty<string>())
                .Concat(!ended ? model.Nodes[node].Options.Keys : Enumerable.Empty<string>())
                .Concat(argument?.Choices ?? []);
            kind = argument?.PathKind ?? CommandPathKind.None;
        }
        return new CommandCompletionResult(candidates.Where(value => value.StartsWith(current, StringComparison.Ordinal)).Select(value => prefix + value), kind, prefix);
    }

    /// <summary>Generates a bash, zsh, fish, or PowerShell completion script.</summary>
    public static string Generate(CommandCatalog catalog, string executable, string shell) => Generate(catalog, executable, shell, "--output");

    /// <summary>Generates completions using the application's configured output selector.</summary>
    public static string Generate(CommandCatalog catalog, string executable, string shell, string outputOptionName)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _ = new ParseSettings(transportOutputOptionName: outputOptionName);
        if (executable.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Completion requires a single executable name containing letters, digits, dots, underscores or hyphens.", nameof(executable));
        var model = new Model(catalog, outputOptionName);
        string function = "_runic_" + executable.Replace('-', '_').Replace('.', '_');
        return shell switch
        {
            "bash" => GenerateBourne(model, executable, function, false),
            "zsh" => GenerateBourne(model, executable, function, true),
            "fish" => GenerateFish(model, executable, function),
            "powershell" or "pwsh" => GeneratePowerShell(model, executable, function),
            _ => throw new ArgumentException("Choose bash, zsh, fish or powershell.", nameof(shell)),
        };
    }

    internal static IReadOnlyList<CommandOptionDescriptor> GlobalOptions(CommandCatalog catalog) =>
        Array.AsReadOnly(Descendants(catalog.Commands).SelectMany(command => command.Options).Where(option => option.IsGlobal)
            .DistinctBy(option => option.Name, StringComparer.Ordinal).ToArray());

    private static IEnumerable<CommandDescriptor> Descendants(IReadOnlyList<CommandDescriptor> commands)
    {
        foreach (CommandDescriptor command in commands)
        { yield return command; foreach (CommandDescriptor child in Descendants(command.Subcommands)) yield return child; }
    }

    private sealed record Value(int Maximum, string[] Choices, CommandPathKind PathKind, int TargetNode = 0);
    private sealed class Node
    {
        internal Dictionary<string, int> Children { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, Value> Options { get; } = new(StringComparer.Ordinal);
        internal List<Value> Arguments { get; } = [];
    }
    private sealed class Model
    {
        internal List<Node> Nodes { get; } = [new()];
        internal int DefaultNode { get; private set; }
        internal Model(CommandCatalog catalog, string output)
        {
            AddOptions(Nodes[0], GlobalOptions(catalog), output);
            AddChildren(0, catalog.Commands);
            if (DefaultNode != 0)
                foreach (var option in Nodes[DefaultNode].Options)
                    Nodes[0].Options.TryAdd(option.Key, option.Value with { TargetNode = DefaultNode });
            if (!catalog.TryGetCommand("completion", out _))
            {
                int completion = Nodes.Count; Nodes.Add(new Node()); Nodes[0].Children.Add("completion", completion);
                Nodes[completion].Arguments.Add(new Value(1, ["bash", "zsh", "fish", "powershell"], CommandPathKind.None));
            }
            // The help command resolves the same command path without suggesting parameter values.
            int helpRoot = Nodes.Count;
            Nodes.Add(new Node()); Nodes[0].Children.Add("help", helpRoot);
            CopyHelp(0, helpRoot);
            void CopyHelp(int source, int target)
            {
                foreach (var child in Nodes[source].Children.ToArray())
                {
                    if (source == 0 && (child.Key == "help" || (child.Key == "completion" && !catalog.TryGetCommand("completion", out _)))) continue;
                    int id = Nodes.Count; Nodes.Add(new Node()); Nodes[target].Children.Add(child.Key, id); CopyHelp(child.Value, id);
                }
            }
            void AddChildren(int parent, IReadOnlyList<CommandDescriptor> commands)
            {
                foreach (CommandDescriptor command in commands)
                {
                    if (command.Help.Hidden) continue;
                    int id = Nodes.Count; var node = new Node(); Nodes.Add(node);
                    Nodes[parent].Children.Add(command.Name, id);
                    foreach (string alias in command.Aliases) Nodes[parent].Children.Add(alias, id);
                    if (ReferenceEquals(command, catalog.DefaultCommand)) DefaultNode = id;
                    AddOptions(node, command.Options, output);
                    foreach (CommandArgumentDescriptor argument in command.Arguments)
                        node.Arguments.Add(CreateValue(argument.Arity, argument.Help, argument.IsSensitive));
                    AddChildren(id, command.Subcommands);
                }
            }
        }
        internal Value? Argument(int node, int position)
        {
            if (node == 0 && DefaultNode != 0) node = DefaultNode;
            foreach (Value value in Nodes[node].Arguments)
            { if (position < value.Maximum) return value; position -= value.Maximum; }
            return null;
        }
        private static void AddOptions(Node node, IReadOnlyList<CommandOptionDescriptor> options, string output)
        {
            foreach (CommandOptionDescriptor option in options)
            {
                if (option.Help.Hidden) continue;
                Value value = CreateValue(option.Arity, option.Help, option.IsSensitive);
                node.Options.TryAdd(option.Name, value);
                foreach (string alias in option.Aliases) node.Options.TryAdd(alias, value);
            }
            node.Options.TryAdd("--help", new Value(0, [], CommandPathKind.None));
            node.Options.TryAdd("-h", new Value(0, [], CommandPathKind.None));
            node.Options.TryAdd("--version", new Value(0, [], CommandPathKind.None));
            node.Options.TryAdd(output, new Value(1, ["human", "json"], CommandPathKind.None));
        }
        private static Value CreateValue(CommandArity arity, CommandHelp help, bool sensitive) => new(arity.Maximum ?? int.MaxValue,
            sensitive || help.Hidden ? [] : help.Choices.Where(choice => !choice.Contains('\n') && !choice.Contains('\r') && !choice.Contains('\0')).ToArray(),
            sensitive || help.Hidden ? CommandPathKind.None : help.PathKind);
    }

    private static string GenerateBourne(Model model, string executable, string function, bool zsh)
    {
        var text = new StringBuilder(zsh ? $"#compdef {executable}\n" : $"# Generated from the {executable} catalog; compatible with Bash 3 and later.\n");
        if (!zsh)
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""
            {{function}}_unquote() {
              local raw="$1" character i escaped=0 quote=''
              unquoted=''
              for (( i=0; i<${#raw}; i++ )); do
                character="${raw:i:1}"
                if [[ "$quote" == "'" ]]; then
                  if [[ "$character" == "'" ]]; then quote=''; else unquoted+="$character"; fi
                elif (( escaped )); then
                  if [[ "$quote" == '"' && "$character" != '$' && "$character" != '`' && "$character" != '"' && "$character" != '\' ]]; then unquoted+='\'; fi
                  unquoted+="$character"; escaped=0
                elif [[ "$character" == '\' ]]; then escaped=1
                elif [[ "$quote" == '"' ]]; then
                  if [[ "$character" == '"' ]]; then quote=''; else unquoted+="$character"; fi
                elif [[ "$character" == "'" || "$character" == '"' ]]; then quote=$character
                else unquoted+="$character"; fi
              done
              active_quote=$quote
            }
            """).Append('\n');
        text.Append(function).Append("_lookup() {\n  found=0; child=0; maximum=0; kind=0; values=()\n  case \"$1:$2:$3\" in\n");
        for (int id = 0; id < model.Nodes.Count; id++)
        {
            Node node = model.Nodes[id];
            foreach (var child in node.Children)
                text.Append("    ").Append(Quote($"c:{id}:{child.Key}")).Append(") found=1; child=").Append(child.Value).Append(" ;;\n");
            foreach (var option in node.Options)
                text.Append("    ").Append(Quote($"o:{id}:{option.Key}")).Append(") found=1; ").Append(BourneValue(option.Value)).Append(" ;;\n");
            text.Append("    ").Append(Quote($"t:{id}:")).Append(") tokens=(").AppendJoin(' ', node.Children.Keys.Concat(node.Options.Keys).Select(Quote)).Append(") ;;\n");
        }
        text.Append("  esac\n  if [[ \"$1\" == a ]]; then\n    case \"$2\" in\n");
        for (int id = 0; id < model.Nodes.Count; id++)
        {
            int argumentNode = id == 0 && model.DefaultNode != 0 ? model.DefaultNode : id;
            text.Append("      ").Append(id).Append(")\n");
            long total = 0;
            foreach (Value value in model.Nodes[argumentNode].Arguments)
            {
                total += value.Maximum;
                text.Append("        if (( $3 < ").Append(total).Append(" )); then found=1; ").Append(BourneValue(value)).Append("; return; fi\n");
            }
            text.Append("        ;;\n");
        }
        text.Append("    esac\n  fi\n}\n");
        text.Append(function).Append("_query() {\n");
        if (zsh) text.Append("  setopt localoptions ksharrays\n");
        text.Append("  local node=0 position=0 ended=0 positional=0 pending='' pending_node=0 consumed=0 word selector i current prefix='' found child maximum kind item\n  local -a values tokens\n  local -a input=(\"$@\")\n");
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""
          for (( i=0; i<${#input[@]}-1; i++ )); do
            word="${input[i]}"
            if [[ -n "$pending" ]]; then
              {{function}}_lookup o "$node" "${word%%=*}"
              if (( !ended )) && { (( found )) || [[ "$word" == -- ]]; }; then pending=''; else
                {{function}}_lookup o "$pending_node" "$pending"
                (( consumed+=1 )); if (( consumed >= maximum )); then pending=''; fi
                continue
              fi
            fi
            if (( !ended )) && [[ "$word" == -- ]]; then ended=1; positional=1; continue; fi
            selector="${word%%=*}"
            {{function}}_lookup o "$node" "$selector"
            if (( !ended && found )); then
              (( child != 0 )) && node=$child
              (( node != 0 )) && positional=1
              consumed=0; [[ "$word" == *=* ]] && consumed=1
              if (( consumed < maximum )); then pending="$selector"; pending_node=$node; fi
              continue
            fi
            {{function}}_lookup c "$node" "$word"
            if (( !ended && !positional && found )); then node=$child; position=0; continue; fi
            if (( node == 0 )); then node={{model.DefaultNode}}; fi
            (( position+=1 )); positional=1
          done
          current="${input[${#input[@]}-1]}"
          if (( !ended )) && [[ "$current" == *=* ]]; then
            selector="${current%%=*}"
            {{function}}_lookup o "$node" "$selector"
            if (( found )); then pending="$selector"; pending_node=$node; prefix="$selector="; current="${current#*=}"; fi
          fi
          [[ "$current" == -* && -z "$prefix" ]] && pending=''
          reply=(); query_kind=0; query_prefix=$prefix; query_current=$current
          if [[ -n "$pending" ]]; then
            {{function}}_lookup o "$pending_node" "$pending"
            query_kind=$kind; tokens=("${values[@]}")
          else
            {{function}}_lookup t "$node" ''
            if (( ended || positional )); then
              local -a filtered=()
              for item in "${tokens[@]}"; do
                {{function}}_lookup o "$node" "$item"
                if (( !ended && found )); then filtered+=("$item"); fi
              done
              tokens=("${filtered[@]}")
            fi
            {{function}}_lookup a "$node" "$position"
            query_kind=$kind; tokens+=("${values[@]}")
          fi
          for item in "${tokens[@]}"; do
            [[ "$item" == "$current"* ]] && reply+=("$prefix$item")
          done
          return 0
        }
        """).Append('\n');
        text.Append(function).Append("() {\n  local query_kind query_prefix query_current item\n  local -a reply\n");
        if (zsh)
        {
            text.Append("  local -a input=(\"${(@)words[2,CURRENT]}\")\n  local quote=\"${compstate[quote]}\"\n  if [[ \"$quote\" == \"'\" || \"$quote\" == '\"' ]]; then input[-1]+=\"$quote\"; fi\n  ").Append(function).Append("_query \"${(@Q)input}\"\n  compadd -- \"${reply[@]}\"\n  if (( query_kind != 0 )); then\n    [[ -n \"$query_prefix\" ]] && compset -P '*='\n    if (( query_kind == 2 )); then _files -/; else _files; fi\n  fi\n}\ncompdef ").Append(function).Append(' ').Append(Quote(executable)).Append('\n');
        }
        else
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""
              local -a input=()
              local i previous='' split_value=0 count unquoted active_quote current_quote='' escaped
              for (( i=1; i<=COMP_CWORD; i++ )); do
                {{function}}_unquote "${COMP_WORDS[i]}"
                item=$unquoted; count=${#input[@]}
                (( i == COMP_CWORD )) && current_quote=$active_quote
                if [[ "$item" == = && $count -gt 0 ]]; then
                  input[count-1]+="="; (( i == COMP_CWORD )) && split_value=1
                elif [[ "$previous" == = && $count -gt 0 ]]; then
                  input[count-1]+="$item"; (( i == COMP_CWORD )) && split_value=1
                else input+=("$item"); fi
                previous=$item
              done
              {{function}}_query "${input[@]}"
              COMPREPLY=("${reply[@]}")
              if (( query_kind != 0 )); then
                local flag=f; (( query_kind == 2 )) && flag=d
                while IFS= read -r item; do
                  [[ -d "$item" && "$item" != */ ]] && item+='/'
                  COMPREPLY+=("$query_prefix$item")
                done < <(compgen -"$flag" -- "$query_current")
              fi
              for (( i=0; i<${#COMPREPLY[@]}; i++ )); do
                item="${COMPREPLY[i]}"
                (( split_value )) && item="${item#"$query_prefix"}"
                if [[ "$current_quote" == "'" ]]; then
                  escaped="${item//\'/\'\\\'\'}"
                elif [[ "$current_quote" == '"' ]]; then
                  escaped="${item//\\/\\\\}"; escaped="${escaped//\"/\\\"}"; escaped="${escaped//\$/\\\$}"; escaped="${escaped//\`/\\\`}"
                else printf -v escaped '%q' "$item"; fi
                COMPREPLY[i]=$escaped
              done
            }
            complete -F {{function}} {{Quote(executable)}}
            """).Append('\n');
        }
        return text.ToString();
    }
    private static string BourneValue(Value value) => $"maximum={value.Maximum}; kind={(int)value.PathKind}; child={value.TargetNode}; values=({string.Join(' ', value.Choices.Select(Quote))})";

    private static string GenerateFish(Model model, string executable, string function)
    {
        var text = new StringBuilder($"# Generated contextual completions for {executable}.\nfunction {function}_lookup\n  switch \"$argv[1]:$argv[2]:$argv[3]\"\n");
        for (int id = 0; id < model.Nodes.Count; id++)
        {
            foreach (var child in model.Nodes[id].Children)
                text.Append("    case ").Append(FishQuote($"c:{id}:{child.Key}")).Append("\n      printf '%s\\n' ").Append(child.Value).Append('\n');
            foreach (var option in model.Nodes[id].Options)
                text.Append("    case ").Append(FishQuote($"o:{id}:{option.Key}")).Append("\n      printf '%s\\n' ").Append(option.Value.Maximum).Append(' ').Append((int)option.Value.PathKind).Append(' ').Append(option.Value.TargetNode).Append(' ').AppendJoin(' ', option.Value.Choices.Select(FishQuote)).Append('\n');
            text.Append("    case ").Append(FishQuote($"t:{id}:")).Append("\n      printf '%s\\n' ").AppendJoin(' ', model.Nodes[id].Children.Keys.Concat(model.Nodes[id].Options.Keys).Select(FishQuote)).Append('\n');
        }
        text.Append("  end\n  if test \"$argv[1]\" = a\n    switch $argv[2]\n");
        for (int id = 0; id < model.Nodes.Count; id++)
        {
            int argumentNode = id == 0 && model.DefaultNode != 0 ? model.DefaultNode : id;
            text.Append("      case ").Append(id).Append('\n');
            long total = 0;
            foreach (Value value in model.Nodes[argumentNode].Arguments)
            {
                total += value.Maximum;
                text.Append("        if test $argv[3] -lt ").Append(total).Append("\n          printf '%s\\n' ").Append(value.Maximum).Append(' ').Append((int)value.PathKind).Append(' ').Append(value.TargetNode).Append(' ').AppendJoin(' ', value.Choices.Select(FishQuote)).Append("\n          return\n        end\n");
            }
        }
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""
            end
          end
        end
        function {{function}}_query
          set -l node 0
          set -l position 0
          set -l positional 0
          set -l ended 0
          set -l pending ''
          set -l pending_node 0
          set -l consumed 0
          set -l current $argv[-1]
          set -l prefix ''
          set -l prior $argv
          set -e prior[-1]
          for word in $prior
            if test -n "$pending"
              set -l pending_pair (string split -m 1 '=' -- "$word")
              set -l recognized ({{function}}_lookup o $node "$pending_pair[1]")
              if test $ended -eq 0; and begin; test (count $recognized) -gt 0; or test "$word" = --; end
                set pending ''
              else
                set -l metadata ({{function}}_lookup o $pending_node "$pending")
                set consumed (math $consumed + 1)
                if test $consumed -ge $metadata[1]
                  set pending ''
                end
                continue
              end
            end
            if test $ended -eq 0; and test "$word" = --
              set ended 1
              set positional 1
              continue
            end
            set -l pair (string split -m 1 '=' -- "$word")
            set -l metadata ({{function}}_lookup o $node "$pair[1]")
            if test $ended -eq 0; and test (count $metadata) -gt 0
              if test $metadata[3] -ne 0
                set node $metadata[3]
              end
              if test $node -ne 0
                set positional 1
              end
              set consumed (math (count $pair) - 1)
              if test $consumed -lt $metadata[1]
                set pending $pair[1]
                set pending_node $node
              end
              continue
            end
            set -l child ({{function}}_lookup c $node "$word")
            if test $ended -eq 0; and test $positional -eq 0; and test (count $child) -gt 0
              set node $child[1]
              set position 0
              continue
            end
            if test $node -eq 0
              set node {{model.DefaultNode}}
            end
            set position (math $position + 1)
            set positional 1
          end
          if test $ended -eq 0; and string match -q '*=*' -- "$current"
            set -l pair (string split -m 1 '=' -- "$current")
            set -l metadata ({{function}}_lookup o $node "$pair[1]")
            if test (count $metadata) -gt 0
              set pending $pair[1]
              set pending_node $node
              set prefix "$pair[1]="
              set current $pair[2]
            end
          end
          if test -z "$prefix"; and string match -q -- '-*' "$current"
            set pending ''
          end
          set -l candidates
          set -l kind 0
          if test -n "$pending"
            set -l metadata ({{function}}_lookup o $pending_node "$pending")
            set kind $metadata[2]
            set candidates $metadata[4..-1]
          else
            for candidate in ({{function}}_lookup t $node '')
              if test $ended -eq 0
                if test $positional -eq 0; or test (count ({{function}}_lookup o $node "$candidate")) -gt 0
                  set -a candidates "$candidate"
                end
              end
            end
            set -l metadata ({{function}}_lookup a $node $position)
            if test (count $metadata) -gt 0
              set kind $metadata[2]
              set -a candidates $metadata[4..-1]
            end
          end
          for candidate in $candidates
            if string match -q -- "(string escape --style=regex -- $current)*" "$candidate"
              printf '%s\n' "$prefix$candidate"
            end
          end
          if test $kind -ne 0
            set -l files (__fish_complete_path "$current")
            for file in $files
              set -l path (string split -m 1 \t -- "$file")[1]
              if test $kind -ne 2; or test -d "$path"
                printf '%s\n' "$prefix$path"
              end
            end
          end
        end
        function {{function}}
          set -l words (string unescape -- (commandline -opc))
          set -e words[1]
          set -l current (string unescape -- (commandline -ct))
          {{function}}_query $words "$current"
        end
        complete -c {{FishQuote(executable)}} -f -a {{FishQuote("(" + function + ")")}}
        """).Append('\n');
        // Prefix matching uses a literal comparison, including shell metacharacters in choices.
        text.Append("function ").Append(function).Append("_available\n  set -l words (string unescape -- (commandline -opc))\n  set -e words[1]\n  contains -- \"$argv[1]\" (").Append(function).Append("_query $words \"$argv[1]\")\nend\n");
        text.Append("function ").Append(function).Append("_value\n  set -l current (commandline -ct)\n  for candidate in (").Append(function).Append(")\n    if string match -q '*=*' -- \"$current\"\n      set -l pair (string split -m 1 '=' -- \"$candidate\")\n      printf '%s\\n' \"$pair[2]\"\n    else\n      printf '%s\\n' \"$candidate\"\n    end\n  end\nend\n");
        foreach (var option in model.Nodes.SelectMany(node => node.Options).DistinctBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string? selector = option.Key.StartsWith("--", StringComparison.Ordinal) ? "-l " + FishQuote(option.Key[2..]) : option.Key.Length == 2 && option.Key[0] == '-' ? "-s " + FishQuote(option.Key[1..]) : null;
            if (selector is null) continue;
            text.Append("complete -c ").Append(FishQuote(executable)).Append(' ').Append(selector)
                .Append(option.Value.Maximum > 0 ? " -r" : "")
                .Append(" -n ").Append(FishQuote(function + "_available " + FishQuote(option.Key)))
                .Append(" -f -a ").Append(FishQuote("(" + function + "_value)")).Append('\n');
        }
        return text.ToString().Replace("if string match -q -- \"(string escape --style=regex -- $current)*\" \"$candidate\"", "if test (string sub -l (string length -- \"$current\") -- \"$candidate\") = \"$current\"", StringComparison.Ordinal);
    }

    private static string GeneratePowerShell(Model model, string executable, string function)
    {
        var text = new StringBuilder($"# Generated contextual completions for {executable}.\nfunction {function}_map {{ param([object[]]$Pairs)\n  $map = [System.Collections.Hashtable]::new([StringComparer]::Ordinal)\n  for ($i = 0; $i -lt $Pairs.Count; $i += 2) {{ $map.Add($Pairs[$i], $Pairs[$i + 1]) }}\n  return ,$map\n}}\nfunction {function}_query {{ param([string[]]$Words)\n  $nodes = @(\n");
        foreach (Node node in model.Nodes)
        {
            text.Append("    @{ Children = (").Append(function).Append("_map -Pairs @(").AppendJoin(", ", node.Children.Select(pair => PowerShellQuote(pair.Key) + ", " + pair.Value)).Append(")); Options = (").Append(function).Append("_map -Pairs @(")
                .AppendJoin(", ", node.Options.Select(pair => PowerShellQuote(pair.Key) + ", " + PowerShellValue(pair.Value))).Append(")); Arguments = @(")
                .AppendJoin(", ", node.Arguments.Select(PowerShellValue)).Append(") }\n");
        }
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""
          )
          $node = 0; $position = 0; $ended = $false; $positional = $false; $pending = $null; $consumed = 0
          for ($i = 0; $i -lt $Words.Count - 1; $i++) {
            $word = $Words[$i]
            if ($null -ne $pending) {
              if (!$ended -and ($word -ceq '--' -or $nodes[$node].Options.ContainsKey($word.Split('=', 2)[0]))) { $pending = $null }
              else { $consumed++; if ($consumed -ge $pending.Maximum) { $pending = $null }; continue }
            }
            if (!$ended -and $word -ceq '--') { $ended = $true; $positional = $true; continue }
            $pair = $word.Split('=', 2)
            if (!$ended -and $nodes[$node].Options.ContainsKey($pair[0])) {
              $consumed = $pair.Count - 1; $option = $nodes[$node].Options[$pair[0]]
              if ($option.Target -ne 0) { $node = $option.Target }
              if ($node -ne 0) { $positional = $true }
              if ($consumed -lt $option.Maximum) { $pending = $option }; continue
            }
            if (!$ended -and !$positional -and $nodes[$node].Children.ContainsKey($word)) { $node = $nodes[$node].Children[$word]; $position = 0; continue }
            if ($node -eq 0) { $node = {{model.DefaultNode}} }; $position++; $positional = $true
          }
          $current = if ($Words.Count) { $Words[-1] } else { '' }; $prefix = ''
          if (!$ended -and $current.Contains('=')) {
            $pair = $current.Split('=', 2)
            if ($nodes[$node].Options.ContainsKey($pair[0])) { $pending = $nodes[$node].Options[$pair[0]]; $prefix = $pair[0] + '='; $current = $pair[1] }
          }
          if ($current.StartsWith('-') -and !$prefix) { $pending = $null }
          $candidates = @(); $kind = 0
          if ($null -ne $pending) { $candidates = $pending.Choices; $kind = $pending.Kind }
          else {
            if (!$ended) { $candidates += @($nodes[$node].Options.Keys); if (!$positional) { $candidates += @($nodes[$node].Children.Keys) } }
            $argumentNode = if ($node -eq 0) { {{model.DefaultNode}} } else { $node }
            foreach ($argument in $nodes[$argumentNode].Arguments) {
              if ($position -lt $argument.Maximum) { $candidates += $argument.Choices; $kind = $argument.Kind; break }; $position -= $argument.Maximum
            }
          }
          @{ Candidates = @($candidates | Where-Object { $_.StartsWith($current, [StringComparison]::Ordinal) } | ForEach-Object { $prefix + $_ } | Sort-Object -Unique -CaseSensitive); Kind = $kind; Prefix = $prefix; Current = $current }
        }
        Register-ArgumentCompleter -Native -CommandName {{PowerShellQuote(executable)}} -ScriptBlock { param($wordToComplete, $commandAst, $cursorPosition)
          $elements = @($commandAst.CommandElements | Select-Object -Skip 1)
          $currentElement = $elements | Where-Object { $_.Extent.StartOffset -lt $cursorPosition -and $_.Extent.EndOffset -ge $cursorPosition } | Select-Object -Last 1
          $words = @($elements | Where-Object { $_.Extent.EndOffset -lt $cursorPosition -and $_ -ne $currentElement } | ForEach-Object { if ($_ -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $_.Value } else { $_.Extent.Text } })
          $current = ''
          if ($currentElement -and $wordToComplete) {
            $current = $currentElement.Extent.Text.Substring(0, $cursorPosition - $currentElement.Extent.StartOffset)
            $tokens = $null; $parseErrors = $null
            $prefixAst = [System.Management.Automation.Language.Parser]::ParseInput(('x ' + $current), [ref]$tokens, [ref]$parseErrors)
            $prefixCommand = $prefixAst.Find({ param($ast) $ast -is [System.Management.Automation.Language.CommandAst] }, $true)
            if ($prefixCommand.CommandElements.Count -eq 2 -and $prefixCommand.CommandElements[1] -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $current = $prefixCommand.CommandElements[1].Value }
          }
          $words += $current
          $result = {{function}}_query -Words $words
          foreach ($candidate in $result.Candidates) {
            $escaped = "'" + $candidate.Replace("'", "''") + "'"
            [System.Management.Automation.CompletionResult]::new($escaped, $candidate, 'ParameterValue', $candidate)
          }
          if ($result.Kind -ne 0) {
            [System.Management.Automation.CompletionCompleters]::CompleteFilename($result.Current) | Where-Object { $result.Kind -ne 2 -or $_.ResultType -eq 'ProviderContainer' } | ForEach-Object { [System.Management.Automation.CompletionResult]::new(($result.Prefix + $_.CompletionText), $_.ListItemText, $_.ResultType, $_.ToolTip) }
          }
        }
        """).Append('\n');
        return text.ToString();
    }

    private static string PowerShellValue(Value value) => "@{ Maximum = " + value.Maximum + "; Kind = " + (int)value.PathKind + "; Target = " + value.TargetNode + "; Choices = @(" + string.Join(", ", value.Choices.Select(PowerShellQuote)) + ") }";
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string FishQuote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
