using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading.Tasks;

namespace DbaToolsStudio.Services;

public class DbaCommandParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public Type ParameterType { get; set; } = typeof(string);
    public bool IsMandatory { get; set; }
    public string HelpMessage { get; set; } = string.Empty;
}

public class DbaCommandMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsPopular { get; set; }
}

public class PowerShellService
{
    public PowerShellService()
    {
        var psModulePath = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
        var winPsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell", "Modules");
        if (!psModulePath.Contains(winPsPath, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PSModulePath", psModulePath + ";" + winPsPath);
        }
    }

    private PowerShell CreatePowerShell()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return PowerShell.Create(iss);
    }

    public async Task<List<DbaCommandMetadata>> GetDbaCommandMetadataAsync()
    {
        var metadataList = new List<DbaCommandMetadata>();
        
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var html = await client.GetStringAsync("https://dbatools.io/commands/");
            
            var pattern = @"<a href=/[^>]+class=command-card[^>]*>.*?<h3 class=command-name>(?<name>[^<]+)</h3>(?<popular>.*?)<p class=command-description>(?<desc>[^<]+)</p>.*?<span class=command-category>Category:\s*(?<cat>[^<]+)</span>";
            var matches = System.Text.RegularExpressions.Regex.Matches(html, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                metadataList.Add(new DbaCommandMetadata
                {
                    Name = match.Groups["name"].Value.Trim(),
                    Description = match.Groups["desc"].Value.Trim(),
                    Category = match.Groups["cat"].Value.Trim(),
                    IsPopular = match.Groups["popular"].Value.Contains("⭐")
                });
            }
        }
        catch
        {
            // Fallback to basic PS if offline
        }

        if (metadataList.Count == 0)
        {
            // Basic fallback
            var commands = await GetDbaCommandsAsync();
            metadataList = commands.Select(c => new DbaCommandMetadata { Name = c, Category = "Uncategorized" }).ToList();
        }

        return metadataList.OrderBy(m => m.Name).ToList();
    }

    public async Task<List<string>> GetDbaCommandsAsync()
    {
        return await Task.Run(() =>
        {
            using var ps = CreatePowerShell();
            ps.AddScript("Import-Module dbatools -ErrorAction SilentlyContinue; Get-Command -Module dbatools | Select-Object -ExpandProperty Name");
            var results = ps.Invoke();

            if (ps.HadErrors)
            {
                var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                throw new Exception($"Failed to retrieve dbatools commands: {errors}");
            }

            return results.Select(r => r.BaseObject.ToString() ?? string.Empty)
                          .Where(n => !string.IsNullOrWhiteSpace(n))
                          .OrderBy(n => n)
                          .ToList();
        });
    }

    public async Task<List<DbaCommandParameterInfo>> GetCommandParametersAsync(string commandName)
    {
        return await Task.Run(() =>
        {
            using var ps = CreatePowerShell();
            ps.AddScript($"(Get-Command {commandName}).Parameters.Values");
            var results = ps.Invoke();

            if (ps.HadErrors)
            {
                var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                throw new Exception($"Failed to retrieve parameters for {commandName}: {errors}");
            }

            var parameterInfos = new List<DbaCommandParameterInfo>();

            // Common parameters to filter out
            var commonParameters = new HashSet<string>(new[]
            {
                "Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction",
                "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable",
                "OutBuffer", "PipelineVariable", "WhatIf", "Confirm"
            }, StringComparer.OrdinalIgnoreCase);

            foreach (var pso in results)
            {
                var name = pso.Properties["Name"]?.Value?.ToString();
                if (string.IsNullOrWhiteSpace(name) || commonParameters.Contains(name))
                    continue;

                var parameterType = pso.Properties["ParameterType"]?.Value as Type ?? typeof(string);
                
                bool isMandatory = false;
                string helpMessage = string.Empty;
                
                // Get Attributes for IsMandatory and HelpMessage
                var attributes = pso.Properties["Attributes"]?.Value as IEnumerable<object>;
                if (attributes != null)
                {
                    foreach (var attrPso in attributes.OfType<PSObject>())
                    {
                        var attr = attrPso.BaseObject;
                        if (attr is ParameterAttribute paramAttr)
                        {
                            if (paramAttr.Mandatory)
                                isMandatory = true;
                            if (!string.IsNullOrWhiteSpace(paramAttr.HelpMessage))
                                helpMessage = paramAttr.HelpMessage;
                        }
                    }
                }

                parameterInfos.Add(new DbaCommandParameterInfo
                {
                    Name = name,
                    ParameterType = parameterType,
                    IsMandatory = isMandatory,
                    HelpMessage = helpMessage
                });
            }

            return parameterInfos;
        });
    }

    public async Task<List<string>> GetCommandExamplesAsync(string commandName)
    {
        return await Task.Run(() =>
        {
            var examples = new List<string>();
            try
            {
                using var ps = CreatePowerShell();
                ps.AddScript($"(Get-Help {commandName} -Examples).examples.example | Select-Object -ExpandProperty code");
                var results = ps.Invoke();
                
                foreach (var r in results)
                {
                    var ex = r?.BaseObject?.ToString();
                    if (!string.IsNullOrWhiteSpace(ex))
                        examples.Add(ex.Trim());
                }
            }
            catch
            {
                // Ignore if examples fail
            }
            return examples;
        });
    }

    public async Task<List<IDictionary<string, object>>> RunDbaCommandAsync(string commandName, Dictionary<string, object> parameters, System.Threading.CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var ps = CreatePowerShell();
            
            using var reg = cancellationToken.Register(() => {
                ps.Stop();
            });
            
            ps.AddScript("Import-Module dbatools; Set-DbatoolsConfig -FullName sql.connection.trustcert -Value $true");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand(commandName);

            foreach (var param in parameters)
            {
                if (param.Value is bool b)
                {
                    if (b)
                    {
                        // Add as switch parameter
                        ps.AddParameter(param.Key);
                    }
                }
                else
                {
                    ps.AddParameter(param.Key, param.Value);
                }
            }

            var results = ps.Invoke();

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Command execution was stopped by the user.");
            }

            if (ps.HadErrors)
            {
                var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                throw new Exception($"Error executing {commandName}: {errors}");
            }

            var outputList = new List<IDictionary<string, object>>();

            foreach (var result in results)
            {
                var expando = new ExpandoObject() as IDictionary<string, object>;
                foreach (var prop in result.Properties)
                {
                    try
                    {
                        var val = prop.Value;
                        if (val == null) continue;
                        
                        var type = val.GetType();
                        // Only add simple types, strings, enums, dates etc. to avoid complex SMO objects cluttering the grid
                        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || 
                            type == typeof(DateTime) || type == typeof(Guid) || type == typeof(TimeSpan))
                        {
                            expando[prop.Name] = val;
                        }
                    }
                    catch
                    {
                        // Ignore properties that throw on access
                    }
                }
                outputList.Add(expando);
            }

            return outputList;
        }, cancellationToken);
    }
}

