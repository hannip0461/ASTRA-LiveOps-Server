# Kubernetes release with Pulumi

Pulumi deploys the repository Helm chart to an existing cluster. Terraform owns Azure resources; Pulumi does not recreate or import them.

```powershell
pulumi login --local
pulumi -C deploy/pulumi stack init dev
pulumi -C deploy/pulumi config set namespace astra
pulumi -C deploy/pulumi config set imageRegistry example.azurecr.io
pulumi -C deploy/pulumi config set --secret kubeconfig (Get-Content $HOME/.kube/config -Raw)
pulumi -C deploy/pulumi preview
```

The Kubernetes Secret referenced by `existingSecret` must be created by the platform secret workflow before deployment.

For an offline provider/resource preview without kubeconfig, set `renderDirectory` instead of `kubeconfig`:

```powershell
pulumi -C deploy/pulumi config set imageRegistry example.azurecr.io
pulumi -C deploy/pulumi config set renderDirectory ../../tmp/pulumi-render
pulumi -C deploy/pulumi preview
```
