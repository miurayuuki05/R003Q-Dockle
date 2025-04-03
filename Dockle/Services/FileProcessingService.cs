using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace Dockle.Services;

public class FileProcessingService{
    private readonly ILogger<FileProcessingService> _logger;

    public FileProcessingService(ILogger<FileProcessingService> logger){
        _logger = logger;
    }

    public bool ValidateProjectStructure(string extractedPath){
        var dockerComposePath = Path.Combine(extractedPath, "docker-compose.yaml");
        bool hasDockerCompose = File.Exists(dockerComposePath);

        _logger.LogInformation($"docker-compose.yaml found: {hasDockerCompose}");

        var folders = Directory.GetDirectories(extractedPath);
        var hasDockerfiles = false;

        _logger.LogInformation($"Found {folders.Length} folders in the extracted directory.");

        foreach (var folder in folders){
            var dockerfilePath = Path.Combine(folder, "dockerfile");
            if (File.Exists(dockerfilePath)){
                hasDockerfiles = true;
                _logger.LogInformation($"Dockerfile found in: {folder}");
            }
            else{
                _logger.LogWarning($"No Dockerfile found in: {folder}");
            }
        }

        if (!hasDockerfiles){
            var dockerfileRootPath = Path.Combine(extractedPath, "dockerfile");
            if (File.Exists(dockerfileRootPath)){
                hasDockerfiles = true;
                _logger.LogInformation($"Dockerfile found in root: {dockerfileRootPath}");
            }
            else{
                _logger.LogWarning($"No Dockerfile found in root: {dockerfileRootPath}");
            }
        }

        if (!hasDockerCompose && !hasDockerfiles){
            _logger.LogError("Invalid structure: No Dockerfile or docker-compose.yaml found.");
            return false;
        }

        _logger.LogInformation("Project structure validated successfully.");
        return true;
    }

    public Dictionary<string, List<string>> CheckDockerSmells(string extractedPath){
        var issues = new Dictionary<string, List<string>>();

        string rootDockerfile = Path.Combine(extractedPath, "dockerfile");
        if (File.Exists(rootDockerfile)){
            _logger.LogInformation("Detected a single Dockerfile in the root directory.");
            issues[rootDockerfile] = AnalyzeDockerfile(rootDockerfile);
        }

        string dockerComposePath = Path.Combine(extractedPath, "docker-compose.yaml");
        if (File.Exists(dockerComposePath)){
            _logger.LogInformation("Detected a docker-compose.yaml in the root. Checking for Dockerfiles in subdirectories...");
            var folders = Directory.GetDirectories(extractedPath);
            foreach (var folder in folders){
                var dockerfilePath = Path.Combine(folder, "dockerfile");
                if (File.Exists(dockerfilePath)){
                    _logger.LogInformation($"Found Dockerfile in: {folder}");
                    issues[dockerfilePath] = AnalyzeDockerfile(dockerfilePath);
                }
                else{
                    _logger.LogWarning($"No Dockerfile found in: {folder}");
                }
            }
        }

        return issues;
    }

    private List<string> AnalyzeDockerfile(string dockerfilePath){
        var warnings = new List<string>();
        string[] lines = File.ReadAllLines(dockerfilePath);

        if (lines.Any(line => line.StartsWith("FROM") && line.Contains(":latest")))
            warnings.Add("❌ Avoid using 'latest' tag in FROM instruction.");

        int runCommands = lines.Count(line => line.Trim().StartsWith("RUN"));
        if (runCommands > 3)
            warnings.Add($"⚠️ Too many RUN commands ({runCommands}). Consider combining them to reduce layers.");

        if (lines.Any(line => line.StartsWith("COPY . /app")))
            warnings.Add("⚠️ Avoid 'COPY . /app'. Use a more selective COPY statement.");

        if (lines.Any(line => line.StartsWith("ADD")) && !lines.Any(line => line.Contains("tar.gz")))
            warnings.Add("⚠️ Prefer 'COPY' instead of 'ADD' unless extracting archives.");

        if (lines.Any(line => line.StartsWith("USER root")))
            warnings.Add("❌ Avoid using 'USER root'. Use a non-root user for better security.");

        if (!lines.Any(line => line.Trim().StartsWith("HEALTHCHECK")))
            warnings.Add("⚠️ Missing 'HEALTHCHECK' instruction. Consider adding a health check to improve container monitoring.");

        int fromCount = lines.Count(line => line.Trim().StartsWith("FROM"));
        if (fromCount == 1)
            warnings.Add("⚠️ Consider using multi-stage builds to reduce the final image size.");

        string dockerIgnorePath = Path.Combine(Path.GetDirectoryName(dockerfilePath) ?? string.Empty, ".dockerignore");
        if (!File.Exists(dockerIgnorePath))
            warnings.Add("⚠️ Missing '.dockerignore' file. This can lead to large image size.");

        if (warnings.Count == 0)
            warnings.Add("✅ No Docker smells detected. Good job!");

        return warnings;
    }

    public List<string> OptimizeDockerCompose(string dockerComposePath){
        var optimizations = new List<string>();
        if (!File.Exists(dockerComposePath)) return optimizations;

        var yaml = new YamlStream();
        using (var reader = new StreamReader(dockerComposePath)){
            yaml.Load(reader);
        }

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        if (root.Children.ContainsKey("services")){
            var services = (YamlMappingNode)root.Children["services"];
            foreach (var service in services.Children){
                var serviceName = service.Key.ToString();
                var serviceConfig = (YamlMappingNode)service.Value;

                if (!serviceConfig.Children.ContainsKey("deploy")){
                    optimizations.Add($"⚠️ Service '{serviceName}' is missing resource limits. Consider adding 'deploy.resources.limits'.");
                }

                if (!serviceConfig.Children.ContainsKey("healthcheck")){
                    optimizations.Add($"⚠️ Service '{serviceName}' is missing a healthcheck. Add one to improve reliability.");
                }
            }
        }

        return optimizations;
    }

    public void OptimizeDockerfiles(string extractedPath){
        string rootDockerfilePath = Path.Combine(extractedPath, "dockerfile");
        if (File.Exists(rootDockerfilePath)){
            _logger.LogInformation($"Optimizing root-level Dockerfile: {rootDockerfilePath}");
            OptimizeDockerfile(rootDockerfilePath);
        }
    
        var folders = Directory.GetDirectories(extractedPath);
        foreach (var folder in folders){
            var dockerfilePath = Path.Combine(folder, "dockerfile");
            if (File.Exists(dockerfilePath)){
                _logger.LogInformation($"Optimizing Dockerfile: {dockerfilePath}");
                OptimizeDockerfile(dockerfilePath);
            }
        }
    }
    
    private void OptimizeDockerfile(string dockerfilePath){
        var backupPath = dockerfilePath + ".bak";
        File.Copy(dockerfilePath, backupPath, true);
        _logger.LogInformation($"Backup created: {backupPath}");

        var lines = File.ReadAllLines(dockerfilePath).ToList();
        var updatedLines = new List<string>();
        string? fromLine = null;
        string workdirLine = "WORKDIR /app";
        string? userLine = null;
        string runLine = "";
        string copyLine = "COPY . .";
        string? exposeLine = null;
        string healthcheckLine = "HEALTHCHECK CMD curl --fail http://localhost || exit 1";
        string? cmdLine = null;

        foreach (var line in lines){
            if (line.StartsWith("FROM") && line.Contains(":latest")){
                fromLine = line.Replace(":latest", ":stable");
                _logger.LogInformation("Updated FROM instruction to avoid 'latest' tag.");
            } else if (line.StartsWith("USER root")){
                userLine = "USER appuser";
                _logger.LogInformation("Replaced 'USER root' with a non-root user.");
            } else if (line.Trim().StartsWith("RUN")){
                runLine += (runLine == "" ? line.Replace("RUN", "").Trim() : " && " + line.Replace("RUN", "").Trim());
            } else if (line.StartsWith("EXPOSE")){
                exposeLine = line;
            } else if (line.StartsWith("CMD")){
                cmdLine = line;
            }
        }

        if (fromLine != null) updatedLines.Add(fromLine);
        if (userLine != null) updatedLines.Add(userLine);
        if (!string.IsNullOrEmpty(runLine)) updatedLines.Add("RUN " + runLine);
        updatedLines.Add(workdirLine);
        updatedLines.Add(copyLine);
        if (exposeLine != null) updatedLines.Add(exposeLine);
        updatedLines.Add(healthcheckLine);
        if (cmdLine != null) updatedLines.Add(cmdLine);

        File.WriteAllLines(dockerfilePath, updatedLines);
        _logger.LogInformation("Dockerfile optimization complete.");
    }
}
