"use client";

import { useState } from "react";
import {
  Mic, Brain, Zap, ChevronRight, Check, User, GraduationCap,
  Server, Cloud, Cpu, ArrowLeft,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { dotnetRequest } from "@/hooks/useTauriInvoke";
import { toast } from "sonner";

// ─── Types ────────────────────────────────────────────────────────────────────

type Step = 1 | 2 | 3;

const CEFR_LEVELS = [
  { value: "A1", label: "A1 – Principiante", desc: "Palabras básicas y frases simples" },
  { value: "A2", label: "A2 – Elemental",    desc: "Situaciones cotidianas sencillas" },
  { value: "B1", label: "B1 – Intermedio",   desc: "Temas familiares con cierta fluidez" },
  { value: "B2", label: "B2 – Avanzado",     desc: "Temas complejos con claridad" },
  { value: "C1", label: "C1 – Competente",   desc: "Expresión fluida y espontánea" },
  { value: "C2", label: "C2 – Maestría",     desc: "Dominio completo del idioma" },
];

const PROVIDERS = [
  { id: "lmstudio", label: "LM Studio",    icon: Server, hint: "Local · OpenAI-compatible", color: "text-blue-600",   endpoint: "http://localhost:1234" },
  { id: "ollama",   label: "Ollama",        icon: Server, hint: "Local · REST API",          color: "text-green-600",  endpoint: "http://localhost:11434" },
  { id: "openai",   label: "OpenAI",        icon: Cloud,  hint: "Cloud · requiere API key",  color: "text-emerald-600", endpoint: "https://api.openai.com" },
  { id: "builtin",  label: "IA Integrada",  icon: Cpu,    hint: "Sin dependencias externas", color: "text-purple-600", endpoint: "" },
] as const;

// ─── Progress indicator ───────────────────────────────────────────────────────

function ProgressIndicator({ step }: { step: Step }) {
  const steps = [
    { n: 1, label: "Bienvenida" },
    { n: 2, label: "Tu perfil" },
    { n: 3, label: "Proveedor IA" },
  ];
  return (
    <div className="flex items-center justify-center gap-2 mb-8">
      {steps.map((s, i) => {
        const done    = step > s.n;
        const active  = step === s.n;
        return (
          <div key={s.n} className="flex items-center gap-2">
            <div className={cn(
              "flex items-center justify-center rounded-full font-medium text-xs transition-all",
              done   ? "w-7 h-7 bg-green-600 text-white" :
              active ? "w-8 h-8 bg-gray-900 text-white" :
                       "w-6 h-6 bg-gray-200 text-gray-400"
            )}>
              {done ? <Check size={13} /> : s.n}
            </div>
            {i < steps.length - 1 && (
              <div className={cn("h-0.5 w-8", done ? "bg-green-600" : "bg-gray-200")} />
            )}
          </div>
        );
      })}
    </div>
  );
}

// ─── Step 1: Welcome ──────────────────────────────────────────────────────────

function WelcomeStep({ onNext }: { onNext: () => void }) {
  return (
    <div className="flex flex-col items-center text-center">
      <div className="w-16 h-16 rounded-2xl bg-gray-900 flex items-center justify-center mb-6">
        <Mic size={28} className="text-white" />
      </div>
      <h1 className="text-2xl font-bold text-gray-900 mb-2">English Learning Assistant</h1>
      <p className="text-gray-500 mb-8 max-w-sm">
        Tu asistente personal para mejorar tu inglés mientras escuchas en tiempo real.
      </p>

      <div className="w-full max-w-sm space-y-3 mb-10 text-left">
        {[
          { icon: Mic,   title: "Transcripción en vivo",  desc: "Capta lo que escuchas usando Windows Live Captions" },
          { icon: Brain, title: "Traducción automática",  desc: "Traduce al instante con tu IA preferida" },
          { icon: Zap,   title: "Responde preguntas",     desc: "Detecta y responde preguntas en inglés en tiempo real" },
        ].map(({ icon: Icon, title, desc }) => (
          <div key={title} className="flex items-start gap-3 p-3 rounded-xl bg-gray-50">
            <div className="w-8 h-8 rounded-lg bg-white border border-gray-200 flex items-center justify-center shrink-0">
              <Icon size={15} className="text-gray-700" />
            </div>
            <div>
              <p className="text-sm font-medium text-gray-900">{title}</p>
              <p className="text-xs text-gray-500">{desc}</p>
            </div>
          </div>
        ))}
      </div>

      <button
        onClick={onNext}
        className="flex items-center gap-2 px-6 py-3 bg-gray-900 text-white rounded-full font-medium hover:bg-gray-700 transition-colors"
      >
        Comenzar <ChevronRight size={16} />
      </button>
    </div>
  );
}

// ─── Step 2: Profile ──────────────────────────────────────────────────────────

function ProfileStep({
  name, setName, level, setLevel, onNext, onBack,
}: {
  name: string; setName: (v: string) => void;
  level: string; setLevel: (v: string) => void;
  onNext: () => void; onBack: () => void;
}) {
  const canContinue = name.trim().length > 0 && level !== "";

  return (
    <div className="flex flex-col w-full max-w-sm">
      <h2 className="text-xl font-bold text-gray-900 mb-1">Tu perfil</h2>
      <p className="text-sm text-gray-500 mb-6">Para personalizar la experiencia de aprendizaje.</p>

      {/* Nombre */}
      <div className="mb-5">
        <label className="text-xs font-semibold uppercase tracking-wider text-gray-500 mb-1.5 flex items-center gap-1.5">
          <User size={11} /> Nombre
        </label>
        <input
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Tu nombre"
          className="w-full border border-gray-200 rounded-xl px-4 py-2.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-gray-900 bg-white"
        />
      </div>

      {/* Nivel CEFR */}
      <div className="mb-8">
        <label className="text-xs font-semibold uppercase tracking-wider text-gray-500 mb-1.5 flex items-center gap-1.5">
          <GraduationCap size={11} /> Nivel de inglés
        </label>
        <div className="grid grid-cols-2 gap-2">
          {CEFR_LEVELS.map((l) => (
            <button
              key={l.value}
              onClick={() => setLevel(l.value)}
              className={cn(
                "flex flex-col items-start px-3 py-2.5 rounded-xl border text-left transition-all",
                level === l.value
                  ? "border-gray-900 bg-gray-900 text-white"
                  : "border-gray-200 hover:border-gray-400 text-gray-900"
              )}
            >
              <span className="text-xs font-bold">{l.value}</span>
              <span className={cn("text-[10px]", level === l.value ? "text-gray-300" : "text-gray-500")}>
                {l.desc.split("·")[0] ?? l.desc}
              </span>
            </button>
          ))}
        </div>
      </div>

      <div className="flex gap-3">
        <button onClick={onBack} className="flex items-center gap-1 px-4 py-2.5 rounded-full border border-gray-200 text-sm text-gray-600 hover:bg-gray-50 transition-colors">
          <ArrowLeft size={14} /> Atrás
        </button>
        <button
          onClick={onNext}
          disabled={!canContinue}
          className={cn(
            "flex-1 flex items-center justify-center gap-2 px-6 py-2.5 rounded-full font-medium text-sm transition-colors",
            canContinue
              ? "bg-gray-900 text-white hover:bg-gray-700"
              : "bg-gray-100 text-gray-400 cursor-not-allowed"
          )}
        >
          Continuar <ChevronRight size={15} />
        </button>
      </div>
    </div>
  );
}

// ─── Step 3: Provider ─────────────────────────────────────────────────────────

function ProviderStep({
  provider, setProvider, endpoint, setEndpoint, apiKey, setApiKey, model, setModel,
  onSave, onBack, saving,
}: {
  provider: string; setProvider: (v: string) => void;
  endpoint: string; setEndpoint: (v: string) => void;
  apiKey: string; setApiKey: (v: string) => void;
  model: string; setModel: (v: string) => void;
  onSave: () => void; onBack: () => void; saving: boolean;
}) {
  const needsKey = provider === "openai" || provider === "custom";
  const needsEndpoint = provider !== "builtin";

  return (
    <div className="flex flex-col w-full max-w-sm">
      <h2 className="text-xl font-bold text-gray-900 mb-1">Proveedor de IA</h2>
      <p className="text-sm text-gray-500 mb-5">Elige cómo quieres procesar traducciones y respuestas.</p>

      {/* Provider cards */}
      <div className="grid grid-cols-2 gap-2 mb-5">
        {PROVIDERS.map(({ id, label, icon: Icon, hint, color }) => (
          <button
            key={id}
            onClick={() => setProvider(id)}
            className={cn(
              "flex flex-col items-start gap-1 px-3 py-3 rounded-xl border text-left transition-all relative",
              provider === id
                ? "border-gray-900 bg-gray-50"
                : "border-gray-200 hover:border-gray-300"
            )}
          >
            <Icon size={16} className={color} />
            <span className="text-xs font-semibold text-gray-900">{label}</span>
            <span className="text-[10px] text-gray-500 leading-tight">{hint}</span>
            {provider === id && (
              <span className="absolute top-2 right-2">
                <Check size={11} className="text-gray-900" />
              </span>
            )}
          </button>
        ))}
      </div>

      {/* Built-in notice */}
      {provider === "builtin" && (
        <p className="text-xs text-purple-700 bg-purple-50 border border-purple-100 rounded-xl px-3 py-2 mb-4">
          Podrás descargar el modelo desde Configuración → IA Integrada después de completar la configuración.
        </p>
      )}

      {/* Endpoint */}
      {needsEndpoint && (
        <div className="mb-3">
          <label className="text-xs font-semibold uppercase tracking-wider text-gray-500 mb-1.5 block">URL del servidor</label>
          <input
            type="text"
            value={endpoint}
            onChange={(e) => setEndpoint(e.target.value)}
            placeholder="http://localhost:1234"
            className="w-full border border-gray-200 rounded-xl px-4 py-2.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-gray-900 bg-white"
          />
        </div>
      )}

      {/* Model */}
      {needsEndpoint && (
        <div className="mb-3">
          <label className="text-xs font-semibold uppercase tracking-wider text-gray-500 mb-1.5 block">Modelo</label>
          <input
            type="text"
            value={model}
            onChange={(e) => setModel(e.target.value)}
            placeholder={provider === "ollama" ? "llama3.2:3b" : "llama-3.2-3b-instruct"}
            className="w-full border border-gray-200 rounded-xl px-4 py-2.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-gray-900 bg-white"
          />
        </div>
      )}

      {/* API Key */}
      {needsKey && (
        <div className="mb-3">
          <label className="text-xs font-semibold uppercase tracking-wider text-gray-500 mb-1.5 block">API Key</label>
          <input
            type="password"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            placeholder="sk-..."
            className="w-full border border-gray-200 rounded-xl px-4 py-2.5 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-gray-900 bg-white"
          />
        </div>
      )}

      <div className="flex gap-3 mt-3">
        <button onClick={onBack} className="flex items-center gap-1 px-4 py-2.5 rounded-full border border-gray-200 text-sm text-gray-600 hover:bg-gray-50 transition-colors">
          <ArrowLeft size={14} /> Atrás
        </button>
        <button
          onClick={onSave}
          disabled={saving}
          className="flex-1 flex items-center justify-center gap-2 px-6 py-2.5 rounded-full bg-gray-900 text-white font-medium text-sm hover:bg-gray-700 transition-colors disabled:opacity-60"
        >
          {saving ? "Guardando…" : <>Empezar a aprender <ChevronRight size={15} /></>}
        </button>
      </div>
    </div>
  );
}

// ─── Main component ───────────────────────────────────────────────────────────

interface Props { onComplete: () => void; }

export function OnboardingFlow({ onComplete }: Props) {
  const [step, setStep]       = useState<Step>(1);
  const [name, setName]       = useState("");
  const [level, setLevel]     = useState("B1");
  const [provider, setProvider] = useState("lmstudio");
  const [endpoint, setEndpoint] = useState("http://localhost:1234");
  const [apiKey, setApiKey]   = useState("");
  const [model, setModel]     = useState("");
  const [saving, setSaving]   = useState(false);

  function handleProviderChange(id: string) {
    setProvider(id);
    const p = PROVIDERS.find((x) => x.id === id);
    if (p?.endpoint) setEndpoint(p.endpoint);
  }

  async function handleSave() {
    setSaving(true);
    try {
      await dotnetRequest("settings", "save", {
        Fields: {
          userName:        name.trim(),
          englishLevel:    level,
          llmProvider:     provider,
          lmStudioBaseUrl: endpoint,
          lmStudioModel:   model,
          lmStudioApiKey:  apiKey,
          onboardingDone:  "true",
        },
      });
      onComplete();
    } catch {
      toast.error("Error al guardar la configuración.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="fixed inset-0 bg-gray-50 z-50 flex flex-col items-center justify-center p-6 overflow-y-auto">
      <div className="w-full max-w-md">
        {step > 1 && <ProgressIndicator step={step} />}

        {step === 1 && (
          <WelcomeStep onNext={() => setStep(2)} />
        )}
        {step === 2 && (
          <ProfileStep
            name={name} setName={setName}
            level={level} setLevel={setLevel}
            onNext={() => setStep(3)}
            onBack={() => setStep(1)}
          />
        )}
        {step === 3 && (
          <ProviderStep
            provider={provider}   setProvider={handleProviderChange}
            endpoint={endpoint}   setEndpoint={setEndpoint}
            apiKey={apiKey}       setApiKey={setApiKey}
            model={model}         setModel={setModel}
            onSave={handleSave}   onBack={() => setStep(2)}
            saving={saving}
          />
        )}
      </div>
    </div>
  );
}
