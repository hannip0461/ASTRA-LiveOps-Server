# Terraform 기반 Azure 인프라

Terraform은 resource group, VNet, private AKS, ACR, Log Analytics와 private PostgreSQL Flexible Server를 소유한다. Pulumi는 Helm application release만 소유해 resource 이중 관리를 방지한다.

State와 secret은 외부 입력으로 관리한다. Azure Storage backend 설정으로 초기화한 뒤 password를 환경 변수로 전달한다.

```powershell
$env:TF_VAR_unique_suffix = "replace1"
$env:TF_VAR_postgres_administrator_password = "replace-with-a-secret-value"
$env:TF_VAR_aks_admin_group_object_ids = '["00000000-0000-0000-0000-000000000000"]'
terraform -chdir=deploy/terraform init -backend-config=backend.hcl
terraform -chdir=deploy/terraform plan
```

CI는 formatting과 validation만 수행하며 cloud resource를 생성하지 않는다.
