# Pulumi 기반 Kubernetes 배포

Pulumi는 기존 Kubernetes cluster에 이 저장소의 Helm chart를 배포한다. Azure resource는 Terraform이 소유하며 Pulumi는 해당 resource를 재생성하거나 import하지 않는다.

```powershell
pulumi login --local
pulumi -C deploy/pulumi stack init dev
pulumi -C deploy/pulumi config set namespace astra
pulumi -C deploy/pulumi config set imageRegistry example.azurecr.io
pulumi -C deploy/pulumi config set --secret kubeconfig (Get-Content $HOME/.kube/config -Raw)
pulumi -C deploy/pulumi preview
```

`existingSecret`이 참조하는 Kubernetes Secret은 배포 전에 platform secret workflow에서 생성해야 한다.

Kubeconfig 없이 provider/resource를 검토하려면 `kubeconfig` 대신 `renderDirectory`를 설정한다.

```powershell
pulumi -C deploy/pulumi config set imageRegistry example.azurecr.io
pulumi -C deploy/pulumi config set renderDirectory ../../tmp/pulumi-render
pulumi -C deploy/pulumi preview
```
