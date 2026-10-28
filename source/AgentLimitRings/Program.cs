using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using MacroDeck.Sdk.Deprecation;

// Report SDK usage explicitly: this plugin calls no deprecated API, so the host need not infer findings from the SDK version.
[assembly: MacroDeckSdkUsage("3.0.0.0", [])]

var plugin = MacroDeckPlugin.CreatePlugin(args)
    .UseMacroDeckLogging()
    .RegisterIntegration<AgentLimitRings.PluginIntegration>()
    .Build();

await plugin.RunAsync();
