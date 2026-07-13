variable "subscription_id" {
  description = "Azure subscription ID. AZURE_SUBSCRIPTION_ID can also supply it."
  type        = string
  default     = null
  nullable    = true
}

variable "location" {
  description = "Azure region for the ASTRA platform."
  type        = string
  default     = "Korea Central"
}

variable "environment" {
  description = "Short environment name."
  type        = string
  default     = "astra"

  validation {
    condition     = can(regex("^[a-z0-9-]{2,12}$", var.environment))
    error_message = "environment must contain 2-12 lowercase letters, numbers, or hyphens."
  }
}

variable "unique_suffix" {
  description = "Lowercase suffix used for globally unique Azure resource names."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]{4,10}$", var.unique_suffix))
    error_message = "unique_suffix must contain 4-10 lowercase letters or numbers."
  }
}

variable "postgres_administrator_password" {
  description = "PostgreSQL administrator password. Supply via TF_VAR_postgres_administrator_password."
  type        = string
  sensitive   = true

  validation {
    condition     = length(var.postgres_administrator_password) >= 16
    error_message = "postgres_administrator_password must contain at least 16 characters."
  }
}

variable "aks_node_count" {
  type    = number
  default = 2

  validation {
    condition     = var.aks_node_count >= 2 && var.aks_node_count <= 5
    error_message = "aks_node_count must be between 2 and 5."
  }
}

variable "aks_node_vm_size" {
  type    = string
  default = "Standard_D2s_v5"
}

variable "aks_admin_group_object_ids" {
  description = "Microsoft Entra group object IDs granted AKS cluster administration."
  type        = list(string)

  validation {
    condition     = length(var.aks_admin_group_object_ids) > 0
    error_message = "At least one AKS administrator group object ID is required."
  }
}

variable "tags" {
  type = map(string)
  default = {
    workload = "astra-liveops"
    managed  = "terraform"
  }
}
