import { invoke } from "@tauri-apps/api/core";

interface DotnetResponse<T> {
  data?: T;
  errorMessage?: string;
}

export async function dotnetRequest<T = unknown>(
  controller: string,
  action: string,
  data?: unknown
): Promise<T> {
  if (typeof window === "undefined" || !("__TAURI_INTERNALS__" in window)) {
    throw new Error("Tauri not available (running in browser)");
  }

  const raw = await invoke<string>("dotnet_request", {
    request: JSON.stringify({ controller, action, data }),
  });

  console.log(`[RPC] controller=${controller}, action=${action}, response=`, raw);

  const res: DotnetResponse<T> = JSON.parse(raw);

  // Intentar leer tanto errorMessage como ErrorMessage para tolerancia de casing
  const errorMsg = res.errorMessage || (res as any).ErrorMessage || (res as any).error;
  if (errorMsg) {
    throw new Error(errorMsg);
  }

  // Intentar leer tanto data como Data
  const responseData = res.data !== undefined ? res.data : (res as any).Data;
  return responseData as T;
}
