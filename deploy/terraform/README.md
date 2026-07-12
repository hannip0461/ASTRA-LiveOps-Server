# Azure foundation with Terraform

Terraform owns the Azure foundation: resource group, VNet, private AKS, ACR, Log Analytics, and private PostgreSQL Flexible Server. Pulumi owns only the Helm application release, preventing dual ownership.

State and secrets are external inputs. Initialize with an Azure Storage backend configuration, then supply the password through the environment:

```powershell
$env:TF_VAR_unique_suffix = "replace1"
$env:TF_VAR_postgres_administrator_password = "replace-with-a-secret-value"
$env:TF_VAR_aks_admin_group_object_ids = '["00000000-0000-0000-0000-000000000000"]'
terraform -chdir=deploy/terraform init -backend-config=backend.hcl
terraform -chdir=deploy/terraform plan
```

No cloud resource is created by CI; CI runs formatting and validation only.
