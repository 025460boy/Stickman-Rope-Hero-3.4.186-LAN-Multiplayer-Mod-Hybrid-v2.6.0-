using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlPatch
{
    /// <summary>
    /// Injects a single `call SRHCoop.CoopBootstrap.Init()` at the start of known
    /// startup methods of Assembly-CSharp. Unity compiles the
    /// [RuntimeInitializeOnLoadMethod] table at build time, so an assembly dropped
    /// into assets/bin/Data/Managed after the build never gets that callback -
    /// this is the reliable equivalent.
    /// </summary>
    internal static class Program
    {
        // (namespace, type, method) - all are MonoBehaviours that run on launch.
        // PrivacyPolicyManager.Awake gets us into the process on the very first scene;
        // GameManager.Awake is the fallback once the world loads.
        private static readonly (string Ns, string Type, string Method)[] Candidates =
        {
            ("Naxeex.Top.Common.PrivacyPolicy", "PrivacyPolicyManager", "Awake"),
            ("Game.Managers", "GameManager", "Awake"),
        };

        private static int Main(string[] args)
        {
            string game = Arg(args, "--game");
            string mod = Arg(args, "--mod");
            string output = Arg(args, "--out");
            if (game == null || mod == null || output == null)
            {
                Console.Error.WriteLine("usage: IlPatch --game Assembly-CSharp.dll --mod SRHCoop.dll --out patched.dll");
                return 2;
            }

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(game)));
            var reader = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };

            using var gameModule = ModuleDefinition.ReadModule(game, reader);
            using var modModule = ModuleDefinition.ReadModule(mod, reader);

            TypeDefinition boot = modModule.GetType("SRHCoop.CoopBootstrap");
            if (boot == null)
            {
                Console.Error.WriteLine("SRHCoop.CoopBootstrap not found in " + mod);
                return 3;
            }
            MethodDefinition init = boot.Methods.FirstOrDefault(m => m.Name == "Init" && m.Parameters.Count == 0);
            if (init == null)
            {
                Console.Error.WriteLine("CoopBootstrap.Init() not found");
                return 3;
            }

            MethodReference imported = gameModule.ImportReference(init);
            int patched = 0;

            foreach (var candidate in Candidates)
            {
                TypeDefinition type = gameModule.GetType(candidate.Ns, candidate.Type);
                if (type == null)
                {
                    Console.WriteLine("  - " + candidate.Type + ": type not found, skipped");
                    continue;
                }
                MethodDefinition method = type.Methods.FirstOrDefault(m => m.Name == candidate.Method);
                if (method == null || !method.HasBody)
                {
                    Console.WriteLine("  - " + candidate.Type + "." + candidate.Method + ": not found, skipped");
                    continue;
                }
                ILProcessor il = method.Body.GetILProcessor();
                if (method.Body.Instructions.Any(i => i.OpCode == OpCodes.Call &&
                        i.Operand is MethodReference mr && mr.Name == "Init" &&
                        mr.DeclaringType.Name == "CoopBootstrap"))
                {
                    Console.WriteLine("  - " + candidate.Type + ": already patched");
                    continue;
                }
                il.InsertBefore(method.Body.Instructions[0], il.Create(OpCodes.Call, imported));
                Console.WriteLine("  + patched " + candidate.Type + "." + candidate.Method);
                patched++;
            }

            if (patched == 0)
            {
                Console.Error.WriteLine("nothing patched");
                return 4;
            }

            gameModule.Write(output);
            Console.WriteLine("wrote " + output + " (" + patched + " hook(s))");
            return 0;
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}

