"use client";

import { useState, useEffect, useCallback } from "react";
import {
  X, Settings, RefreshCw, Check, AlertCircle, Loader2,
  Bot, User, Cpu, Download, Trash2,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { dotnetRequest } from "@/hooks/useTauriInvoke";
import { listen } from "@tauri-apps/api/event";
import { toast } from "sonner";

// ─── Types ────────────────────────────────────────────────────────────────────

interface LlmSettings { provider: string; endpoint: string; model: string; apiKey: string; }
interface AppSettings  { studentName: string; cefrLevel: string; llm: LlmSettings; }
interface TestResult   { ok: boolean; message: string; models: string[]; }

interface HardwareInfo {
  cpuName: string; cpuCores: number; totalRamGb: number;
  gpuName: string; gpuType: "nvidia" | "amd" | "integrated";
  freeDiskGb: number;
}
interface BuiltInModel {
  id: string; displayName: string; description: string;
  fileSizeBytes: number; fileSizeMb: number; requiredRamGb: number;
  isRecommended: boolean; status: "not_downloaded" | "downloading" | "available" | "incomplete" | "corrupted";
}
interface BuiltInStatus {
  hardware: HardwareInfo;
  models: BuiltInModel[];
  recommendedModelId: string;
}
interface DownloadProgress {
  model: string; progress: number; downloaded_mb: number;
  total_mb: number; speed_mbps: number; status: string;
}

// ─── Constants ────────────────────────────────────────────────────────────────

const PROVIDERS = [
  { id: "builtin",  label: "Integrado", hint: "sin dependencias · auto",  endpoint: "",                    needsKey: false },
  { id: "lmstudio", label: "LM Studio", hint: "local · OpenAI-compat",    endpoint: "http://localhost:1234", needsKey: false },
  { id: "ollama",   label: "Ollama",    hint: "local · REST API",          endpoint: "http://localhost:11434", needsKey: false },
  { id: "openai",   label: "OpenAI",    hint: "cloud · API key",           endpoint: "https://api.openai.com", needsKey: true },
  { id: "custom",   label: "Custom",    hint: "OpenAI-compatible",         endpoint: "", needsKey: true },
] as const;

const CEFR_LEVELS = ["A1", "A2", "B1", "B2", "C1", "C2"] as const;

type Tab = "builtin" | "llm" | "general";

// ─── Component ────────────────────────────────────────────────────────────────

interface Props { open: boolean; onClose: () => void; tab?: Tab; }

export function SettingsPanel({ open, onClose, tab: requestedTab }: Props) {
  const [tab, setTab]         = useState<Tab>("builtin");
  const [settings, setSettings] = useState<AppSettings>({
    studentName: "Estudiante",
    cefrLevel: "B1",
    llm: { provider: "lmstudio", endpoint: "http://localhost:1234", model: "", apiKey: "" },
  });
  const [testStatus, setTestStatus]   = useState<"idle" | "loading" | "ok" | "error">("idle");
  const [testResult, setTestResult]   = useState<TestResult | null>(null);
  const [saving, setSaving]           = useState(false);

  // Modelos disponibles del servidor (LM Studio / Ollama)
  const [availableModels, setAvailableModels] = useState<string[]>([]);
  const [loadingModels, setLoadingModels]     = useState(false);

  // Built-in AI state
  const [builtIn, setBuiltIn]                 = useState<BuiltInStatus | null>(null);
  const [loadingBuiltIn, setLoadingBuiltIn]   = useState(false);
  const [downloadProgress, setDownloadProgress] = useState<Record<string, DownloadProgress>>({});
  const [deletingId, setDeletingId]           = useState<string | null>(null);

  useEffect(() => {
    if (open && requestedTab) setTab(requestedTab);
  }, [open, requestedTab]);

  // ── Listen for download progress events ──────────────────────────────────
  useEffect(() => {
    if (!open) return;
    const unlisten = listen<DownloadProgress>("builtin-ai-progress", (ev) => {
      const p = ev.payload;
      setDownloadProgress((prev) => ({ ...prev, [p.model]: p }));
      // Once a model becomes available/error, refresh statuses
      if (p.status === "available" || p.status === "corrupted" || p.status === "error") {
        loadBuiltIn();
      }
    });
    return () => { unlisten.then((fn) => fn()); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  // ── Load settings & built-in status when panel opens ────────────────────
  function loadBuiltIn() {
    setLoadingBuiltIn(true);
    dotnetRequest<BuiltInStatus>("builtInAi", "getStatus")
      .then((s) => setBuiltIn(s))
      .catch(() => {})
      .finally(() => setLoadingBuiltIn(false));
  }

  useEffect(() => {
    if (!open) return;
    dotnetRequest<{
      studentName: string; cefrLevel: string;
      llm: { provider: string; endpoint: string; model: string; apiKey: string };
    }>("settings", "get")
      .then((s) => {
        const provider = s.llm?.provider ?? "lmstudio";
        setSettings({
          studentName: s.studentName ?? "Estudiante",
          cefrLevel:   s.cefrLevel   ?? "B1",
          llm: {
            provider,
            endpoint: s.llm?.endpoint ?? "http://localhost:1234",
            model:    s.llm?.model    ?? "",
            apiKey:   s.llm?.apiKey   ?? "",
          },
        });
        setTestStatus("idle");
        setTestResult(null);
        fetchAvailableModels(provider, s.llm?.endpoint ?? "http://localhost:1234", s.llm?.apiKey ?? "");
      })
      .catch(() => {});

    loadBuiltIn();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  // ── Modelos disponibles del servidor ─────────────────────────────────────
  async function fetchAvailableModels(provider: string, endpoint = settings.llm.endpoint, apiKey = settings.llm.apiKey) {
    if (provider !== "lmstudio" && provider !== "ollama") {
      setAvailableModels([]);
      return;
    }
    setLoadingModels(true);
    try {
      const res = await dotnetRequest<TestResult>("settings", "testConnection", {
        Provider: provider,
        Endpoint: endpoint,
        ApiKey: apiKey || null,
        Model: null,
      });
      setAvailableModels(res.models ?? []);
    } catch {
      setAvailableModels([]);
    } finally {
      setLoadingModels(false);
    }
  }

  // ── Provider switch ───────────────────────────────────────────────────────
  function selectProvider(id: string) {
    const p = PROVIDERS.find((x) => x.id === id)!;
    setSettings((prev) => ({
      ...prev,
      llm: { ...prev.llm, provider: id, endpoint: p.endpoint || prev.llm.endpoint },
    }));
    setTestStatus("idle");
    setTestResult(null);
    fetchAvailableModels(id, p.endpoint || settings.llm.endpoint, settings.llm.apiKey);
  }

  // ── Test connection ───────────────────────────────────────────────────────
  async function handleTest() {
    setTestStatus("loading");
    setTestResult(null);
    try {
      const res = await dotnetRequest<TestResult>("settings", "testConnection", {
        Provider: settings.llm.provider,
        Endpoint: settings.llm.endpoint,
        ApiKey: settings.llm.apiKey || null,
        Model: settings.llm.model || null,
      });
      setTestStatus(res.ok ? "ok" : "error");
      setTestResult(res);
    } catch (e: unknown) {
      setTestStatus("error");
      setTestResult({ ok: false, message: (e as Error).message ?? "Error", models: [] });
    }
  }

  // ── Save ──────────────────────────────────────────────────────────────────
  async function handleSave() {
    setSaving(true);
    try {
      await dotnetRequest("settings", "save", {
        Fields: {
          userName:        settings.studentName,
          englishLevel:    settings.cefrLevel,
          llmProvider:     settings.llm.provider,
          lmStudioBaseUrl: settings.llm.endpoint,
          lmStudioModel:   settings.llm.model,
          lmStudioApiKey:  settings.llm.apiKey,
        },
      });
      toast.success("Configuración guardada y aplicada.");
      onClose();
    } catch (e: unknown) {
      toast.error("Error al guardar: " + (e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  // ── Built-in AI actions ──────────────────────────────────────────────────
  async function startDownload(modelId: string) {
    await dotnetRequest("builtInAi", "startDownload", { ModelId: modelId });
    setDownloadProgress((prev) => ({
      ...prev,
      [modelId]: { model: modelId, progress: 0, downloaded_mb: 0, total_mb: 0, speed_mbps: 0, status: "downloading" },
    }));
  }

  async function cancelDownload() {
    await dotnetRequest("builtInAi", "cancelDownload");
    setDownloadProgress({});
    loadBuiltIn();
  }

  async function deleteModel(modelId: string) {
    setDeletingId(modelId);
    try {
      await dotnetRequest("builtInAi", "deleteModel", { ModelId: modelId });
      loadBuiltIn();
    } finally {
      setDeletingId(null);
    }
  }

  if (!open) return null;

  const currentProvider = PROVIDERS.find((p) => p.id === settings.llm.provider) ?? PROVIDERS[0];

  return (
    <div role="dialog" aria-modal="true" aria-label="Configuración" className="absolute inset-0 z-50 flex flex-col bg-background">
      {/* ── Header ── */}
      <div
        className="flex items-center justify-between h-9 px-4 border-b border-border shrink-0 select-none"
        data-tauri-drag-region
      >
        <div className="flex items-center gap-2">
          <Settings size={13} className="text-muted-foreground" />
          <span className="text-xs font-semibold text-foreground">Configuración</span>
        </div>
        <button
          onClick={onClose}
          aria-label="Cerrar configuración"
          className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
        >
          <X size={13} />
        </button>
      </div>

      {/* ── Tab bar ── */}
      <div className="flex gap-0 border-b border-border shrink-0 px-2">
        {(["builtin", "llm", "general"] as Tab[]).map((t) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={cn(
              "flex items-center gap-1.5 px-3 py-2 text-xs font-medium border-b-2 -mb-px transition-colors",
              tab === t
                ? "border-primary text-foreground"
                : "border-transparent text-muted-foreground hover:text-foreground"
            )}
          >
            {t === "builtin" ? <Cpu size={12} /> : t === "llm" ? <Bot size={12} /> : <User size={12} />}
            {t === "builtin" ? "IA Integrada" : t === "llm" ? "Proveedor" : "General"}
          </button>
        ))}
      </div>

      {/* ── Content ── */}
      <div className="flex-1 overflow-y-auto px-4 py-4 space-y-5">

        {/* ─── Built-in AI tab ─────────────────────────────────────────────── */}
        {tab === "builtin" && (
          <>
            {/* Hardware card — estilo Meetily */}
            <section className="rounded-xl border border-border bg-card p-5 space-y-3">
              <h3 className="text-sm font-semibold text-foreground">Tu dispositivo</h3>
              {loadingBuiltIn && !builtIn ? (
                <div className="flex items-center gap-2 text-xs text-muted-foreground">
                  <Loader2 size={12} className="animate-spin" /> Detectando hardware…
                </div>
              ) : builtIn?.hardware ? (
                <div className="space-y-2">
                  <HardwareRow label="CPU"        value={builtIn.hardware.cpuName} />
                  <HardwareRow label="RAM"        value={`${builtIn.hardware.totalRamGb} GB`} />
                  <HardwareRow label="GPU"        value={builtIn.hardware.gpuName} />
                  <HardwareRow label="Disco libre" value={`${builtIn.hardware.freeDiskGb} GB`} />
                </div>
              ) : null}
            </section>

            {/* Info */}
            <p className="text-xs text-muted-foreground leading-relaxed">
              Descarga un modelo que se ejecuta <strong className="text-foreground">directamente en tu equipo</strong> — sin LM Studio, Ollama ni internet.
            </p>

            {/* Models */}
            <section className="space-y-2">
              <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">Modelos</p>

              {loadingBuiltIn && !builtIn ? (
                <div className="flex items-center gap-2 text-xs text-muted-foreground">
                  <Loader2 size={12} className="animate-spin" /> Cargando…
                </div>
              ) : (
                builtIn?.models.map((m) => {
                  const prog = downloadProgress[m.id];
                  const effectiveStatus = prog?.status === "downloading" ? "downloading" : m.status;

                  return (
                    <ModelCard
                      key={m.id}
                      model={m}
                      effectiveStatus={effectiveStatus}
                      progress={prog}
                      isDeleting={deletingId === m.id}
                      onDownload={() => startDownload(m.id)}
                      onCancel={cancelDownload}
                      onDelete={() => deleteModel(m.id)}
                      onSelect={() => {
                        selectProvider("builtin");
                        setSettings((prev) => ({ ...prev, llm: { ...prev.llm, model: m.id } }));
                        setTab("llm");
                      }}
                    />
                  );
                })
              )}
            </section>
          </>
        )}

        {/* ─── LLM / Provider tab ──────────────────────────────────────────── */}
        {tab === "llm" && (
          <>
            {/* Provider cards */}
            <section className="space-y-2">
              <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">Proveedor</p>
              <div className="grid grid-cols-2 gap-2">
                {PROVIDERS.map((p) => {
                  const active = settings.llm.provider === p.id;
                  return (
                    <button
                      key={p.id}
                      onClick={() => selectProvider(p.id)}
                      className={cn(
                        "relative flex flex-col items-start gap-0.5 px-3 py-2.5 rounded-lg border text-left transition-all",
                        active ? "border-primary bg-primary/10" : "border-border hover:border-muted-foreground/60"
                      )}
                    >
                      <span className={cn("text-xs font-semibold", active ? "text-primary" : "text-foreground")}>
                        {p.label}
                      </span>
                      <span className="text-[10px] text-muted-foreground">{p.hint}</span>
                      {active && <span className="absolute top-2 right-2"><Check size={11} className="text-primary" /></span>}
                    </button>
                  );
                })}
              </div>
            </section>

            {/* Endpoint (hidden for built-in) */}
            {settings.llm.provider !== "builtin" && (
              <section className="space-y-1.5">
                <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">Endpoint URL</p>
                <input
                  type="text"
                  value={settings.llm.endpoint}
                  onChange={(e) => {
                    setSettings((p) => ({ ...p, llm: { ...p.llm, endpoint: e.target.value } }));
                    setTestStatus("idle");
                  }}
                  placeholder="http://localhost:1234"
                  className="w-full bg-muted border border-border rounded-md px-3 py-2 text-xs text-foreground placeholder:text-muted-foreground focus:outline-none focus:ring-1 focus:ring-primary"
                />
              </section>
            )}

            {/* Model */}
            {settings.llm.provider !== "builtin" && (
              <section className="space-y-1.5">
                <div className="flex items-center justify-between">
                  <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">Modelo</p>
                  {(settings.llm.provider === "lmstudio" || settings.llm.provider === "ollama") && (
                    <button
                      onClick={() => fetchAvailableModels(settings.llm.provider)}
                      disabled={loadingModels}
                      className="flex items-center gap-1 text-[10px] text-muted-foreground hover:text-foreground transition-colors disabled:opacity-40"
                    >
                      {loadingModels ? <Loader2 size={10} className="animate-spin" /> : <RefreshCw size={10} />}
                      {loadingModels ? "Cargando…" : availableModels.length > 0 ? `${availableModels.length} modelos` : "Cargar modelos"}
                    </button>
                  )}
                </div>

                {(settings.llm.provider === "lmstudio" || settings.llm.provider === "ollama") && availableModels.length > 0 ? (
                  <select
                    value={settings.llm.model}
                    onChange={(e) => setSettings((p) => ({ ...p, llm: { ...p.llm, model: e.target.value } }))}
                    className="w-full bg-muted border border-border rounded-md px-3 py-2 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
                  >
                    {!settings.llm.model && <option value="">— Seleccionar modelo —</option>}
                    {availableModels.map((m) => (
                      <option key={m} value={m}>{m}</option>
                    ))}
                  </select>
                ) : (
                  <input
                    type="text"
                    value={settings.llm.model}
                    onChange={(e) => setSettings((p) => ({ ...p, llm: { ...p.llm, model: e.target.value } }))}
                    placeholder="llama-3.2-3b-instruct"
                    className="w-full bg-muted border border-border rounded-md px-3 py-2 text-xs text-foreground placeholder:text-muted-foreground focus:outline-none focus:ring-1 focus:ring-primary font-mono"
                  />
                )}
              </section>
            )}

            {/* API Key */}
            {currentProvider.needsKey && (
              <section className="space-y-1.5">
                <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">API Key</p>
                <input
                  type="password"
                  value={settings.llm.apiKey}
                  onChange={(e) => setSettings((p) => ({ ...p, llm: { ...p.llm, apiKey: e.target.value } }))}
                  placeholder="sk-..."
                  className="w-full bg-muted border border-border rounded-md px-3 py-2 text-xs text-foreground placeholder:text-muted-foreground focus:outline-none focus:ring-1 focus:ring-primary font-mono"
                />
              </section>
            )}

            {/* Test connection (not for built-in) */}
            {settings.llm.provider !== "builtin" && (
              <section className="space-y-2">
                <button
                  onClick={handleTest}
                  disabled={testStatus === "loading" || !settings.llm.endpoint}
                  className="flex items-center gap-1.5 px-3 py-1.5 rounded-md border border-border text-xs text-muted-foreground hover:text-foreground hover:border-muted-foreground transition-colors disabled:opacity-40"
                >
                  {testStatus === "loading" ? <Loader2 size={12} className="animate-spin" /> : <RefreshCw size={12} />}
                  Probar conexión
                </button>

                {testStatus === "ok" && (
                  <div className="flex items-center gap-1.5 text-xs text-green-400">
                    <Check size={12} /> {testResult?.message}
                  </div>
                )}
                {testStatus === "error" && (
                  <div className="flex items-start gap-1.5 text-xs text-destructive">
                    <AlertCircle size={12} className="mt-0.5 shrink-0" />
                    <span>{testResult?.message}</span>
                  </div>
                )}
              </section>
            )}

            {/* Built-in info when selected */}
            {settings.llm.provider === "builtin" && (
              <div className="rounded-lg border border-border bg-muted/20 p-3 text-xs text-muted-foreground space-y-1">
                <p className="font-semibold text-foreground">Proveedor Integrado</p>
                <p>El modelo seleccionado en la pestaña &quot;IA Integrada&quot; se usará para traducción local.</p>
                <button
                  onClick={() => setTab("builtin")}
                  className="text-primary hover:underline text-[11px]"
                >
                  Ir a IA Integrada →
                </button>
              </div>
            )}
          </>
        )}

        {/* ─── General tab ─────────────────────────────────────────────────── */}
        {tab === "general" && (
          <>
            <section className="space-y-1.5">
              <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">Tu nombre</p>
              <input
                type="text"
                value={settings.studentName}
                onChange={(e) => setSettings((p) => ({ ...p, studentName: e.target.value }))}
                placeholder="Estudiante"
                className="w-full bg-muted border border-border rounded-md px-3 py-2 text-xs text-foreground placeholder:text-muted-foreground focus:outline-none focus:ring-1 focus:ring-primary"
              />
            </section>

            <section className="space-y-2">
              <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">Nivel CEFR</p>
              <div className="grid grid-cols-6 gap-1.5">
                {CEFR_LEVELS.map((lvl) => (
                  <button
                    key={lvl}
                    onClick={() => setSettings((p) => ({ ...p, cefrLevel: lvl }))}
                    className={cn(
                      "py-2 rounded-md text-xs font-semibold border transition-colors",
                      settings.cefrLevel === lvl
                        ? "border-primary bg-primary/10 text-primary"
                        : "border-border text-muted-foreground hover:border-muted-foreground hover:text-foreground"
                    )}
                  >
                    {lvl}
                  </button>
                ))}
              </div>
              <p className="text-[10px] text-muted-foreground leading-relaxed">
                Personaliza las explicaciones y ayudas durante la sesión según tu nivel actual de inglés.
              </p>
            </section>
          </>
        )}
      </div>

      {/* ── Footer ── */}
      <div className="flex items-center justify-end gap-2 px-4 py-3 border-t border-border shrink-0">
        <button
          onClick={onClose}
          className="px-3 py-1.5 rounded-md text-xs text-muted-foreground hover:text-foreground transition-colors"
        >
          Cancelar
        </button>
        <button
          onClick={handleSave}
          disabled={saving}
          className="flex items-center gap-1.5 px-4 py-1.5 rounded-md bg-primary text-primary-foreground text-xs font-medium hover:bg-primary/90 transition-colors disabled:opacity-50"
        >
          {saving && <Loader2 size={11} className="animate-spin" />}
          Guardar
        </button>
      </div>
    </div>
  );
}

// ─── Sub-components ───────────────────────────────────────────────────────────

function HardwareRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center justify-between">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span className="text-xs text-foreground font-medium truncate max-w-[160px] text-right">{value}</span>
    </div>
  );
}

/** Replica exacta del renderDownloadCard de Meetily, adaptada a dark theme */
function ModelCard({
  model, effectiveStatus, progress, isDeleting,
  onDownload, onCancel, onDelete, onSelect,
}: {
  model: BuiltInModel;
  effectiveStatus: string;
  progress?: DownloadProgress;
  isDeleting: boolean;
  onDownload: () => void;
  onCancel: () => void;
  onDelete: () => void;
  onSelect: () => void;
}) {
  const sizeMb    = Math.round(model.fileSizeBytes / (1024 * 1024));
  const isDownloading = effectiveStatus === "downloading";
  const isAvailable   = effectiveStatus === "available";
  const isWaiting     = effectiveStatus === "not_downloaded" || effectiveStatus === "incomplete";
  const isError       = effectiveStatus === "corrupted";

  // progress.progress viene como 0-1, convertir a 0-100
  const pct = progress ? Math.round(progress.progress * 100) : 0;

  return (
    <div className={cn(
      "rounded-xl border p-5 transition-colors",
      model.isRecommended ? "border-primary/30 bg-primary/5" : "border-border bg-card",
    )}>
      {/* Header: icon | title+size | status */}
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-3">
          {/* Ícono circular — igual que Meetily */}
          <div className="w-10 h-10 rounded-full bg-muted flex items-center justify-center shrink-0">
            <Cpu size={18} className="text-muted-foreground" />
          </div>
          <div>
            <div className="flex items-center gap-1.5">
              <h3 className="text-sm font-medium text-foreground">{model.displayName}</h3>
              {model.isRecommended && (
                <span className="text-[9px] px-1.5 py-0.5 rounded-full bg-primary/20 text-primary font-semibold leading-none">
                  recomendado
                </span>
              )}
            </div>
            <p className="text-xs text-muted-foreground">~{sizeMb} MB · mín. {model.requiredRamGb} GB RAM</p>
          </div>
        </div>

        {/* Estado — exacto a Meetily */}
        <div className="shrink-0">
          {isWaiting && (
            <span className="text-xs text-muted-foreground">Esperando...</span>
          )}
          {isDownloading && (
            <Loader2 size={18} className="text-foreground animate-spin" />
          )}
          {isAvailable && (
            <div className="w-6 h-6 rounded-full bg-green-500/20 flex items-center justify-center">
              <Check size={14} className="text-green-500" />
            </div>
          )}
          {isError && (
            <span className="text-xs text-destructive">Error</span>
          )}
        </div>
      </div>

      {/* Barra de progreso — igual que Meetily */}
      {(isDownloading || isAvailable) && (
        <div className="space-y-2 mb-3">
          <div className="w-full h-2 bg-muted rounded-full overflow-hidden">
            <div
              className="h-full rounded-full transition-all duration-300 bg-gradient-to-r from-foreground/60 to-foreground"
              style={{ width: `${isAvailable ? 100 : pct}%` }}
            />
          </div>
          <div className="flex items-center justify-between text-xs text-muted-foreground">
            <span>
              {isAvailable
                ? `${sizeMb}.0 MB / ${sizeMb}.0 MB`
                : `${progress?.downloaded_mb.toFixed(1) ?? "0.0"} MB / ${progress?.total_mb.toFixed(1) ?? sizeMb + ".0"} MB`}
            </span>
            <div className="flex items-center gap-2">
              {progress && progress.speed_mbps > 0 && !isAvailable && (
                <span>{progress.speed_mbps.toFixed(1)} MB/s</span>
              )}
              <span className="font-semibold text-foreground">{isAvailable ? 100 : pct}%</span>
            </div>
          </div>
        </div>
      )}

      {/* Acciones */}
      <div className="flex gap-2">
        {isAvailable ? (
          <>
            <button
              onClick={onSelect}
              className="flex-1 flex items-center justify-center gap-1.5 py-1.5 rounded-lg bg-foreground text-background text-xs font-medium hover:bg-foreground/90 transition-colors"
            >
              <Check size={11} /> Usar este modelo
            </button>
            <button
              onClick={onDelete}
              disabled={isDeleting}
              title="Eliminar modelo"
              className="flex items-center justify-center px-2.5 py-1.5 rounded-lg border border-border text-muted-foreground hover:text-destructive hover:border-destructive/40 transition-colors disabled:opacity-40"
            >
              {isDeleting ? <Loader2 size={12} className="animate-spin" /> : <Trash2 size={12} />}
            </button>
          </>
        ) : isDownloading ? (
          <button
            onClick={onCancel}
            className="w-full py-1.5 rounded-lg border border-border text-xs text-muted-foreground hover:text-destructive hover:border-destructive/40 transition-colors"
          >
            Cancelar descarga
          </button>
        ) : (
          <button
            onClick={onDownload}
            className="w-full flex items-center justify-center gap-1.5 py-1.5 rounded-lg bg-foreground text-background text-xs font-medium hover:bg-foreground/90 transition-colors"
          >
            <Download size={11} /> Descargar
          </button>
        )}
      </div>

      {/* Error state */}
      {isError && (
        <div className="mt-3 p-3 bg-destructive/10 border border-destructive/20 rounded-lg">
          <p className="text-xs text-destructive font-medium">El archivo descargado está corrupto.</p>
          <button
            onClick={onDownload}
            className="mt-2 w-full py-1.5 rounded-lg bg-foreground text-background text-xs font-medium hover:bg-foreground/90 transition-colors"
          >
            Reintentar
          </button>
        </div>
      )}
    </div>
  );
}
