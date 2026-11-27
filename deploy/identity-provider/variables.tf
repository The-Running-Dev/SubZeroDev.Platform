variable "state_passphrase" {
  description = "Encrypts the state and plans stored in R2."
  type        = string
  sensitive   = true
}

variable "portainer_url" {
  type    = string
  default = "https://containers.subzerodev.com"
}

variable "portainer_api_key" {
  type      = string
  sensitive = true
}

variable "cf_access_client_id" {
  description = "Cloudflare Access service token in front of Portainer."
  type        = string
  sensitive   = true
}

variable "cf_access_client_secret" {
  type      = string
  sensitive = true
}

variable "admin_route" {
  description = "Hostname label under subzerodev.com that reaches Keycloak's admin side during an admin session, or null outside one (README.md, \"Admin sessions\"). Pass it with -var for the session only."
  type        = string
  default     = null

  validation {
    condition     = var.admin_route == null || can(regex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", coalesce(var.admin_route, "x")))
    error_message = "admin_route must be a single DNS label (lowercase letters, digits and '-')."
  }
  validation {
    condition     = var.admin_route == null || !contains(["id", "@"], coalesce(var.admin_route, "x"))
    error_message = "admin_route must not be the public hostname."
  }
}
