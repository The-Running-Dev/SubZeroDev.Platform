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

variable "dispatch_token" {
  description = "Token with Actions write on the Infra repository only; the deploy jobs use it to start the Infra deploy workflow."
  type        = string
  sensitive   = true
}

variable "health_route" {
  description = "Hostname label under subzerodev.com for the edge's own health check. Not decided yet, so it has no default; set it in the var-file."
  type        = string
}

variable "initial_image" {
  description = "Edge image the stack is created with, ghcr.io/the-running-dev/platform-game-edge@sha256:..., from a main build of container.yml. Deploys own it afterwards."
  type        = string

  validation {
    condition     = can(regex("^ghcr\\.io/the-running-dev/platform-game-edge@sha256:[0-9a-f]{64}$", var.initial_image))
    error_message = "initial_image must be ghcr.io/the-running-dev/platform-game-edge pinned by digest."
  }
}
