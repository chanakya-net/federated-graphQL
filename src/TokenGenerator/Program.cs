using System.Collections;
using SoR.TokenGenerator;

// All logic lives in TokenGeneratorApp.Run so the tests can drive it in-process.
var env = new Dictionary<string, string?>(StringComparer.Ordinal);
foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
    env[(string)variable.Key] = (string?)variable.Value;

return TokenGeneratorApp.Run(args, env, Console.Out, Console.Error);
