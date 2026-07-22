"use client";

import { useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { toast } from "sonner";
import { ArrowLeft, ArrowRight, Check, ChevronRight, Cloud, Cpu, Download, GraduationCap, Loader2, Mic, Server, Sparkles, User, XCircle } from "lucide-react";
import { cn } from "@/lib/utils";
import { dotnetRequest } from "@/hooks/useTauriInvoke";

type Step = 1 | 2 | 3 | 4;
type Provider = "lmstudio" | "ollama" | "openai" | "builtin";

const LEVELS = [
  ["A1", "Principiante", "Palabras básicas y frases simples"],
  ["A2", "Elemental", "Situaciones cotidianas sencillas"],
  ["B1", "Intermedio", "Temas familiares con cierta fluidez"],
  ["B2", "Avanzado", "Temas complejos con claridad"],
  ["C1", "Competente", "Expresión fluida y espontánea"],
  ["C2", "Maestría", "Dominio completo del idioma"],
] as const;

const PROVIDERS: { id: Provider; label: string; hint: string; icon: typeof Server; endpoint: string }[] = [
  { id: "lmstudio", label: "LM Studio", hint: "Local · OpenAI-compatible", icon: Server, endpoint: "http://localhost:1234" },
  { id: "ollama", label: "Ollama", hint: "Local · REST API", icon: Server, endpoint: "http://localhost:11434" },
  { id: "openai", label: "OpenAI", hint: "Cloud · API key", icon: Cloud, endpoint: "https://api.openai.com" },
  { id: "builtin", label: "IA Integrada", hint: "Sin dependencias externas", icon: Cpu, endpoint: "" },
];

interface Props { onComplete: () => void; }
interface Model { id: string; displayName: string; fileSizeMb: number; status: string; isRecommended: boolean; }
interface Progress { model: string; progress: number; downloaded_mb: number; total_mb: number; speed_mbps: number; status: string; }

function ProgressBar({ step }: { step: Step }) {
  const labels = ["Bienvenida", "Preparación", "Configuración", "Modelos"];
  return <div className="mb-8 flex items-center justify-center gap-2">
    {labels.map((label, i) => {
      const n = i + 1; const done = step > n; const active = step === n;
      return <div key={label} className="flex items-center gap-2">
        <div title={label} className={cn("flex h-7 w-7 items-center justify-center rounded-full text-[10px] font-semibold", done ? "bg-green-600 text-white" : active ? "bg-gray-900 text-white" : "bg-gray-200 text-gray-400")}>{done ? <Check size={13} /> : n}</div>
        {n < labels.length && <div className={cn("h-0.5 w-8", done ? "bg-green-600" : "bg-gray-200")} />}
      </div>;
    })}
  </div>;
}

function Frame({ title, description, step, children, onBack }: { title: string; description: string; step: Step; children: React.ReactNode; onBack?: () => void }) {
  return <div role="dialog" aria-modal="true" aria-labelledby="onboarding-title" className="fixed inset-0 z-50 overflow-y-auto overscroll-contain bg-gray-50 px-4 py-5 sm:px-6 sm:py-8">
    <div className="mx-auto flex min-h-full w-full max-w-2xl flex-col justify-center py-2">
      {step > 1 && <ProgressBar step={step} />}
      <div className="mb-7 text-center"><h1 id="onboarding-title" className="break-words text-2xl font-semibold tracking-tight text-gray-900 sm:text-3xl">{title}</h1><p className="mx-auto mt-2 max-w-md text-sm text-gray-600">{description}</p></div>
      <div className="mx-auto w-full max-w-xl">{children}</div>
      {onBack && <button onClick={onBack} className="mx-auto mt-6 flex items-center gap-1 text-xs text-gray-500 hover:text-gray-900"><ArrowLeft size={13} /> Atrás</button>}
    </div>
  </div>;
}

function Welcome({ next }: { next: () => void }) {
  return <Frame step={1} title="Bienvenido a English Learning Assistant" description="Prepárate para practicar inglés con transcripción, traducción y ayuda inteligente en tiempo real.">
    <div className="space-y-3">
      {[{ icon: Mic, title: "Transcripción en vivo", text: "Captura lo que escuchas con Windows Live Captions." }, { icon: Sparkles, title: "Traducción automática", text: "Traduce al instante con el proveedor que prefieras." }, { icon: Cpu, title: "Procesamiento local", text: "Puedes usar un modelo integrado sin depender de servicios externos." }].map(({ icon: Icon, title, text }) => <div key={title} className="flex gap-3 rounded-xl border border-gray-200 bg-white p-4 shadow-sm"><div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-gray-100"><Icon size={15} /></div><div><p className="text-sm font-medium text-gray-900">{title}</p><p className="text-xs text-gray-500">{text}</p></div></div>)}
      <button onClick={next} className="mt-5 flex h-11 w-full items-center justify-center gap-2 rounded-xl bg-gray-900 text-sm font-medium text-white hover:bg-gray-700">Comenzar configuración <ChevronRight size={16} /></button>
      <p className="text-center text-xs text-gray-500">La configuración puede tardar unos minutos.</p>
    </div>
  </Frame>;
}

function Overview({ next, provider }: { next: () => void; provider: Provider }) {
  return <Frame step={2} title="Esto es lo que vamos a preparar" description="Puedes cambiar estas opciones más adelante desde Configuración.">
    <div className="space-y-3 rounded-xl border border-gray-200 bg-white p-5">
      {[{ n: 1, title: "Tu perfil", text: "Nombre y nivel CEFR para personalizar la experiencia." }, { n: 2, title: "Proveedor de IA", text: provider === "builtin" ? "Modelo integrado ejecutado directamente en tu equipo." : "Conexión con tu proveedor local o en la nube." }, { n: 3, title: "Modelo y estado", text: provider === "builtin" ? "Descarga el modelo recomendado para tu hardware." : "Comprueba que tu proveedor esté listo para usar." }].map((item) => <div key={item.n} className="flex gap-3"><div className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-gray-900 text-xs text-white">{item.n}</div><div><p className="text-sm font-medium text-gray-900">{item.title}</p><p className="text-xs text-gray-500">{item.text}</p></div></div>)}
    </div>
    <button onClick={next} className="mt-7 flex h-11 w-full items-center justify-center gap-2 rounded-xl bg-gray-900 text-sm font-medium text-white hover:bg-gray-700">Continuar <ArrowRight size={15} /></button>
  </Frame>;
}

interface ConfigurationProps {
  name: string;
  setName: (value: string) => void;
  level: string;
  setLevel: (value: string) => void;
  provider: Provider;
  setProvider: (value: Provider) => void;
  endpoint: string;
  setEndpoint: (value: string) => void;
  model: string;
  setModel: (value: string) => void;
  apiKey: string;
  setApiKey: (value: string) => void;
  next: () => void;
  back: () => void;
}

function Configuration({ name, setName, level, setLevel, provider, setProvider, endpoint, setEndpoint, model, setModel, apiKey, setApiKey, next, back }: ConfigurationProps) {
    const endpointValid = provider === "builtin" || (() => {
    try {
      const url = new URL(endpoint);
      return (url.protocol === "http:" || url.protocol === "https:") && !url.username && !url.password;
    } catch {
      return false;
    }
  })();
  const canContinue =
    name.trim().length > 0 &&
    name.trim().length <= 80 &&
    endpointValid &&
    (provider === "builtin" || model.trim().length > 0) &&
    (provider !== "openai" || apiKey.trim().length > 0);
  const [availableModels, setAvailableModels] = useState<string[]>([]);
  const [loadingModels, setLoadingModels] = useState(false);
  const [modelDiscoveryMessage, setModelDiscoveryMessage] = useState("");
  const [modelDiscoveryError, setModelDiscoveryError] = useState("");
  const modelRequest = useRef(0);

  async function fetchAvailableModels(p: string) {
    const requestId = ++modelRequest.current;
    if (p !== "lmstudio" && p !== "ollama") {
      setAvailableModels([]);
      setLoadingModels(false);
      setModelDiscoveryMessage("");
      setModelDiscoveryError("");
      return;
    }

    try {
      const url = new URL(endpoint.trim());
      if ((url.protocol !== "http:" && url.protocol !== "https:") || url.username || url.password) throw new Error();
    } catch {
      setAvailableModels([]);
      setLoadingModels(false);
      setModelDiscoveryMessage("");
      setModelDiscoveryError("Ingresa una URL HTTP o HTTPS válida para buscar modelos.");
      return;
    }

    setLoadingModels(true);
    setModelDiscoveryMessage("");
    setModelDiscoveryError("");
    try {
      const res = await dotnetRequest<{ ok: boolean; message: string; models: string[] }>("settings", "testConnection", {
        Provider: p,
        Endpoint: endpoint.trim(),
        ApiKey: apiKey || null,
        Model: null,
      });
      if (requestId !== modelRequest.current) return;

      const discoveredModels = res.models ?? [];
      setAvailableModels(discoveredModels);
      if (!res.ok) {
        setModelDiscoveryError(res.message || "No se pudieron consultar los modelos.");
      } else if (discoveredModels.length === 0) {
        setModelDiscoveryMessage("El gestor respondió, pero no tiene modelos descargados.");
      } else {
        setModelDiscoveryMessage(`${discoveredModels.length} modelo${discoveredModels.length === 1 ? "" : "s"} disponible${discoveredModels.length === 1 ? "" : "s"}.`);
        if (model && !discoveredModels.includes(model)) setModel("");
      }
    } catch (reason) {
      if (requestId === modelRequest.current) {
        setAvailableModels([]);
        setModelDiscoveryError(reason instanceof Error ? reason.message : String(reason));
      }
    } finally {
      if (requestId === modelRequest.current) setLoadingModels(false);
    }
  }

  useEffect(() => {
    if (provider !== "lmstudio" && provider !== "ollama") return;
    const timer = window.setTimeout(() => fetchAvailableModels(provider), 450);
    return () => window.clearTimeout(timer);
  }, [provider, endpoint]);

  return <Frame step={3} title="Configura tu experiencia" description="Estos datos se guardarán al finalizar el onboarding." onBack={back}>
    <div className="space-y-5">
      <div><label className="mb-1.5 flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-wider text-gray-500"><User size={11} /> Nombre</label><input value={name} maxLength={80} autoComplete="name" onChange={(e) => setName(e.target.value)} placeholder="Tu nombre" className="w-full rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-gray-900" /></div>
      <div><label className="mb-1.5 flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-wider text-gray-500"><GraduationCap size={11} /> Nivel de inglés</label><div className="grid grid-cols-1 gap-2 sm:grid-cols-2">{LEVELS.map(([value, label, desc]) => <button key={value} onClick={() => setLevel(value)} className={cn("rounded-xl border px-3 py-2 text-left", level === value ? "border-gray-900 bg-gray-900 text-white" : "border-gray-200 text-gray-900 hover:border-gray-400")}><span className="text-xs font-bold">{value}</span><span className={cn("block text-[10px]", level === value ? "text-gray-300" : "text-gray-500")}>{label} · {desc}</span></button>)}</div></div>
      <div><label className="mb-1.5 block text-[10px] font-semibold uppercase tracking-wider text-gray-500">Proveedor de IA</label><div className="grid grid-cols-1 gap-2 sm:grid-cols-2">{PROVIDERS.map(({ id, label, hint, icon: Icon, endpoint: defaultEndpoint }) => <button key={id} onClick={() => { if (id !== provider) { setProvider(id); setModel(""); setAvailableModels([]); setModelDiscoveryMessage(""); setModelDiscoveryError(""); if (defaultEndpoint) setEndpoint(defaultEndpoint); } }} className={cn("relative rounded-xl border p-3 text-left", provider === id ? "border-gray-900 bg-gray-50" : "border-gray-200 hover:border-gray-400")}><Icon size={15} /><span className="mt-1 block text-xs font-semibold">{label}</span><span className="block text-[10px] text-gray-500">{hint}</span>{provider === id && <Check size={12} className="absolute right-2 top-2" />}</button>)}</div></div>
      {provider !== "builtin" && (
        <>
          <input type="url" value={endpoint} onChange={(e) => setEndpoint(e.target.value)} placeholder="URL del servidor" className="w-full rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-gray-900" />
          <div>
            <div className="mb-1.5 flex items-center justify-between">
              <label className="text-[10px] font-semibold uppercase tracking-wider text-gray-500">Modelo</label>
              {(provider === "lmstudio" || provider === "ollama") && (
                <button type="button" onClick={() => fetchAvailableModels(provider)} disabled={loadingModels} className="flex items-center gap-1 text-[10px] text-gray-400 hover:text-gray-700 disabled:opacity-40">
                  {loadingModels ? <Loader2 size={10} className="animate-spin" /> : <>{availableModels.length > 0 ? `${availableModels.length} modelos` : "Cargar"}</>}
                </button>
              )}
            </div>
            {(provider === "lmstudio" || provider === "ollama") && availableModels.length > 0 ? (
              <select aria-label="Modelo descargado" value={model} onChange={(e) => setModel(e.target.value)} className="w-full rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-gray-900">
                {!model && <option value="">— Seleccionar modelo —</option>}
                {availableModels.map((m) => <option key={m} value={m}>{m}</option>)}
              </select>
            ) : (
              <input value={model} onChange={(e) => setModel(e.target.value)} placeholder={loadingModels ? "Buscando modelos descargados…" : "Nombre del modelo"} className="w-full rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-gray-900" />
            )}
            {(provider === "lmstudio" || provider === "ollama") && modelDiscoveryMessage && <p role="status" className="mt-1.5 text-[10px] text-green-700">{modelDiscoveryMessage}</p>}
            {(provider === "lmstudio" || provider === "ollama") && modelDiscoveryError && <p role="alert" className="mt-1.5 text-[10px] text-red-600">{modelDiscoveryError}</p>}
          </div>
          {provider === "openai" && <input type="password" value={apiKey} onChange={(e) => setApiKey(e.target.value)} placeholder="API key" className="w-full rounded-xl border border-gray-200 bg-white px-4 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-gray-900" />}
        </>
      )}
      <button type="button" disabled={!canContinue} onClick={next} className={cn("flex h-11 w-full items-center justify-center gap-2 rounded-xl text-sm font-medium", canContinue ? "bg-gray-900 text-white hover:bg-gray-700" : "bg-gray-100 text-gray-400")}>Continuar <ArrowRight size={15} /></button>
    </div>
  </Frame>;
}

function ModelSetup({ provider, saving, onComplete, onBack }: { provider: Provider; saving: boolean; onComplete: (modelId?: string) => void; onBack: () => void }) {
  const [models, setModels] = useState<Model[]>([]);
  const [modelId, setModelId] = useState("");
  const selectedModel = useRef("");
  const [progress, setProgress] = useState<Progress | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(provider === "builtin");

  useEffect(() => {
    selectedModel.current = modelId;
  }, [modelId]);

  useEffect(() => {
    if (provider !== "builtin") return;

    let active = true;
    let cleanup: (() => void) | undefined;

    dotnetRequest<{ models: Model[]; recommendedModelId: string }>("builtInAi", "getStatus")
      .then((status) => {
        if (!active) return;
        setModels(status.models);
        const selected = status.models.find((item) => item.isRecommended)?.id ?? status.recommendedModelId;
        setModelId(selected);
      })
      .catch((reason) => {
        if (active) setError(reason instanceof Error ? reason.message : String(reason));
      })
      .finally(() => {
        if (active) setLoading(false);
      });

    listen<Progress>("builtin-ai-progress", (event) => {
      if (event.payload.model !== selectedModel.current) return;
      setProgress(event.payload);

      if (event.payload.status === "available") {
        setError("");
        setModels((current) => current.map((item) =>
          item.id === event.payload.model ? { ...item, status: "available" } : item
        ));
      }

      if (event.payload.status === "error" || event.payload.status === "corrupted") {
        setError("No se pudo descargar y validar el modelo. Revisa tu conexión e inténtalo otra vez.");
      }
    }).then((unlisten) => {
      if (active) cleanup = unlisten;
      else unlisten();
    });

    return () => {
      active = false;
      cleanup?.();
    };
  }, [provider]);

  const current = models.find((item) => item.id === modelId);
  const ready = current?.status === "available" || progress?.status === "available";
  const downloading = current?.status === "downloading" || progress?.status === "downloading";
  const percent = Math.max(0, Math.min(100, Math.round((progress?.progress ?? 0) * 100)));

  async function startDownload() {
    if (!modelId) return;
    setError("");
    setProgress({
      model: modelId,
      progress: 0,
      downloaded_mb: 0,
      total_mb: current?.fileSizeMb ?? 0,
      speed_mbps: 0,
      status: "downloading",
    });

    try {
      await dotnetRequest("builtInAi", "startDownload", { ModelId: modelId });
    } catch (reason) {
      setProgress(null);
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  }

  async function cancelDownload() {
    try {
      await dotnetRequest("builtInAi", "cancelDownload");
      setProgress(null);
      setModels((items) => items.map((item) =>
        item.id === modelId ? { ...item, status: "incomplete" } : item
      ));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  }

  if (provider !== "builtin") {
    return (
      <Frame step={4} title="Todo listo para comprobar" description="Validaremos la conexión y el modelo antes de guardar." onBack={saving ? undefined : onBack}>
        <div className="rounded-xl border border-green-200 bg-green-50 p-5 text-center">
          <Check className="mx-auto mb-2 text-green-600" />
          <p className="text-sm font-medium text-green-900">Proveedor configurado</p>
          <p className="mt-1 text-xs text-green-800">La comprobación puede tardar algunos segundos.</p>
        </div>
        <button type="button" disabled={saving} onClick={() => onComplete()} className="mt-6 flex h-11 w-full items-center justify-center gap-2 rounded-xl bg-gray-900 text-sm font-medium text-white hover:bg-gray-700 disabled:cursor-wait disabled:opacity-60">
          {saving ? <Loader2 size={15} className="animate-spin" /> : <Check size={15} />}
          {saving ? "Comprobando…" : "Comprobar y finalizar"}
        </button>
      </Frame>
    );
  }

  return (
    <Frame step={4} title="Prepara tu modelo local" description="La descarga solo comenzará cuando la confirmes. El archivo se guarda en los datos de tu usuario." onBack={saving || downloading ? undefined : onBack}>
      <div className="rounded-xl border border-gray-200 bg-white p-4 shadow-sm sm:p-5">
        {loading ? (
          <div className="flex min-h-28 items-center justify-center gap-2 text-sm text-gray-500">
            <Loader2 size={16} className="animate-spin" />
            Detectando el modelo recomendado…
          </div>
        ) : (
          <>
            <label htmlFor="onboarding-local-model" className="mb-1.5 block text-xs font-semibold text-gray-700">Modelo local</label>
            <select
              id="onboarding-local-model"
              value={modelId}
              disabled={downloading || saving}
              onChange={(event) => {
                setModelId(event.target.value);
                setProgress(null);
                setError("");
              }}
              className="h-11 w-full rounded-xl border border-gray-300 bg-white px-3 text-sm outline-none focus:border-gray-900 focus:ring-2 focus:ring-gray-900/10 disabled:opacity-60"
            >
              {models.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.displayName}{item.isRecommended ? " · recomendado" : ""}
                </option>
              ))}
            </select>

            <div className="mt-4 flex min-w-0 items-center gap-3 rounded-xl bg-gray-50 p-4">
              <Cpu className="shrink-0 text-gray-700" />
              <div className="min-w-0">
                <p className="truncate text-sm font-medium text-gray-900">{current?.displayName ?? "Modelo recomendado"}</p>
                <p className="text-xs text-gray-500">{current ? "~" + Math.round(current.fileSizeMb) + " MB" : "Esperando información…"}</p>
              </div>
            </div>

            {(downloading || ready) && (
              <div className="mt-5" aria-live="polite">
                <div className="h-2 overflow-hidden rounded-full bg-gray-100">
                  <div className="h-full rounded-full bg-gray-900 transition-[width]" style={{ width: String(ready ? 100 : percent) + "%" }} />
                </div>
                <div className="mt-2 flex flex-wrap justify-between gap-2 text-xs text-gray-500">
                  <span>{ready ? "Descarga completada y validada" : "Descargando…"}</span>
                  <span>{ready ? "100%" : String(percent) + "%"}</span>
                </div>
              </div>
            )}

            {error && (
              <p role="alert" className="mt-4 flex items-start gap-2 rounded-lg bg-red-50 p-3 text-xs leading-5 text-red-700">
                <XCircle size={14} className="mt-0.5 shrink-0" />
                {error}
              </p>
            )}

            <div className="mt-5 flex flex-col gap-2 sm:flex-row">
              {downloading ? (
                <button type="button" onClick={() => void cancelDownload()} className="h-11 flex-1 rounded-xl border border-gray-300 text-sm font-medium text-gray-700 hover:bg-gray-50">
                  Cancelar descarga
                </button>
              ) : !ready ? (
                <button type="button" disabled={!modelId} onClick={() => void startDownload()} className="flex h-11 flex-1 items-center justify-center gap-2 rounded-xl bg-gray-900 text-sm font-medium text-white hover:bg-gray-700 disabled:bg-gray-200 disabled:text-gray-500">
                  <Download size={15} />
                  {error ? "Reintentar descarga" : "Descargar modelo"}
                </button>
              ) : null}

              <button type="button" disabled={!ready || !modelId || saving} onClick={() => onComplete(modelId)} className="flex h-11 flex-1 items-center justify-center gap-2 rounded-xl bg-gray-900 text-sm font-medium text-white hover:bg-gray-700 disabled:cursor-not-allowed disabled:bg-gray-100 disabled:text-gray-400">
                {saving ? <Loader2 size={15} className="animate-spin" /> : <Check size={15} />}
                {saving ? "Validando…" : "Validar y finalizar"}
              </button>
            </div>
          </>
        )}
      </div>
    </Frame>
  );
}

export function OnboardingFlow({ onComplete }: Props) {
  const [step, setStep] = useState<Step>(1); const [name, setName] = useState(""); const [level, setLevel] = useState("B1"); const [provider, setProvider] = useState<Provider>("builtin"); const [endpoint, setEndpoint] = useState("http://localhost:1234"); const [model, setModel] = useState(""); const [apiKey, setApiKey] = useState(""); const [saving, setSaving] = useState(false);
  async function finish(modelId?: string) {
    setSaving(true);
    try {
      if (provider === "builtin" && modelId) {
        const health = await dotnetRequest<{ ok: boolean; error?: string }>("builtInAi", "testModel", { ModelId: modelId });
        if (!health.ok) throw new Error(health.error || "El modelo no respondió a la prueba de inferencia.");
      } else if (provider !== "builtin") {
        const health = await dotnetRequest<{ ok: boolean; message?: string }>("settings", "testConnection", { Provider: provider, Endpoint: endpoint.trim(), ApiKey: apiKey.trim() || null, Model: model.trim() });
        if (!health.ok) throw new Error(health.message || "El proveedor no respondió correctamente.");
      }
      await dotnetRequest("settings", "save", { Fields: { userName: name.trim(), englishLevel: level, llmProvider: provider, lmStudioBaseUrl: endpoint, lmStudioModel: provider === "builtin" ? (modelId ?? model) : model, lmStudioApiKey: apiKey, onboardingDone: true } });
      onComplete();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      toast.error("No se pudo validar o guardar la configuración: " + message);
    } finally {
      setSaving(false);
    }
  }
  if (step === 1) return <Welcome next={() => setStep(2)} />;
  if (step === 2) return <Overview provider={provider} next={() => setStep(3)} />;
  if (step === 3) return <Configuration {...{ name, setName, level, setLevel, provider, setProvider, endpoint, setEndpoint, model, setModel, apiKey, setApiKey }} next={() => setStep(4)} back={() => setStep(2)} />;
  return <ModelSetup provider={provider} saving={saving} onComplete={finish} onBack={() => setStep(3)} />;
}