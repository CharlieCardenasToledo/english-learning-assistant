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

  const res: DotnetResponse<T> = JSON.parse(raw);

  if (res.errorMessage) {
    throw new Error(res.errorMessage);
  }

  return res.data as T;
}
