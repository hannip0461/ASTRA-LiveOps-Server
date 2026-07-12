using Pulumi;
using Pulumi.Kubernetes;
using Pulumi.Kubernetes.Helm.V3;
using HelmReleaseArgs = Pulumi.Kubernetes.Types.Inputs.Helm.V3.ReleaseArgs;

return await Deployment.RunAsync(() =>
{
    var config = new Pulumi.Config();
    var kubernetesNamespace = config.Get("namespace") ?? "astra";
    var imageRegistry = config.Require("imageRegistry").TrimEnd('/');
    var imageTag = config.Get("imageTag") ?? "0.1.0";
    var existingSecret = config.Get("existingSecret") ?? "astra-liveops-secrets";
    var otlpEndpoint = config.Get("otlpEndpoint") ?? "";
    var renderDirectory = config.Get("renderDirectory");

    var providerArgs = new ProviderArgs();
    if (string.IsNullOrWhiteSpace(renderDirectory))
    {
        providerArgs.KubeConfig = config.RequireSecret("kubeconfig");
    }
    else
    {
        providerArgs.RenderYamlToDirectory = renderDirectory;
    }

    var provider = new Provider("astra-kubernetes", providerArgs);

    var release = new Release("astra-liveops", new HelmReleaseArgs
    {
        Name = "astra-liveops",
        Chart = "../helm/astra-liveops",
        Namespace = kubernetesNamespace,
        CreateNamespace = true,
        Atomic = true,
        CleanupOnFail = true,
        WaitForJobs = true,
        Timeout = 600,
        Values = new InputMap<object>
        {
            ["fullnameOverride"] = "astra-liveops",
            ["global"] = new Dictionary<string, object>
            {
                ["existingSecret"] = existingSecret,
                ["otlpEndpoint"] = otlpEndpoint
            },
            ["components"] = new Dictionary<string, object>
            {
                ["silo"] = ImageValues(imageRegistry, "astra-silo", imageTag),
                ["api"] = ImageValues(imageRegistry, "astra-api", imageTag),
                ["tcpGateway"] = ImageValues(imageRegistry, "astra-tcp-gateway", imageTag),
                ["worker"] = ImageValues(imageRegistry, "astra-worker", imageTag),
                ["admin"] = ImageValues(imageRegistry, "astra-admin", imageTag)
            }
        }
    }, new CustomResourceOptions { Provider = provider });

    return new Dictionary<string, object?>
    {
        ["namespace"] = kubernetesNamespace,
        ["releaseName"] = release.Name,
        ["releaseStatus"] = release.Status
    };
});

static Dictionary<string, object> ImageValues(string registry, string name, string tag) =>
    new()
    {
        ["image"] = new Dictionary<string, object>
        {
            ["repository"] = $"{registry}/{name}",
            ["tag"] = tag
        }
    };
