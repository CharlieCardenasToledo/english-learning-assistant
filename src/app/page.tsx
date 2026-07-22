"use client";

import { useState, useCallback, useEffect, useRef } from "react";
import { toast } from "sonner";
import { TitleBar } from "@/components/TitleBar/TitleBar";
import { TranscriptionPanel } from "@/components/TranscriptionPanel/TranscriptionPanel";
import { TranslationPanel } from "@/components/TranslationPanel/TranslationPanel";
import { QuestionPanel } from "@/components/QuestionPanel/QuestionPanel";
import { SessionControls } from "@/components/SessionControls/SessionControls";
import { SettingsPanel } from "@/components/SettingsPanel/SettingsPanel";
import { OnboardingFlow } from "@/components/Onboarding/OnboardingFlow";
import { Sidebar } from "@/components/Sidebar/Sidebar";
import { useCaptionEvents } from "@/hooks/useCaptionEvents";
import { dotnetRequest } from "@/hooks/useTauriInvoke";
import { listen } from "@tauri-apps/api/event";
import { AnimatePresence, motion } from "framer-motion";
import { AlertTriangle, ArrowRight, Download, RefreshCw, Settings2, X } from "lucide-react";
import type { TranscriptionLine, TranslationLine, DetectedQuestion, QuestionConversationItem } from "@/types";

type TranslationError = { provider?: string; reason: string; details?: string };

export default function HomePage() {
  const [transcriptionLines, setTranscriptionLines] = useState<TranscriptionLine[]>([]);
  const [partialText, setPartialText] = useState("");
  const [translationLines, setTranslationLines] = useState<TranslationLine[]>([]);
  const [questionItems, setQuestionItems] = useState<QuestionConversationItem[]>([]);
  const [isSessionActive, setIsSessionActive] = useState(false);
  const [sessionStartTime, setSessionStartTime] = useState<Date | null>(null);
  const [sessionDuration, setSessionDuration] = useState("00:00:00");
  const [questionCount, setQuestionCount] = useState(0);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [barHeights, setBarHeights] = useState(["12px", "20px", "12px"]);
  const [isStarting, setIsStarting] = useState(false);
  const [translationError, setTranslationError] = useState<TranslationError | null>(null);
  const [settingsTab, setSettingsTab] = useState<"builtin" | "llm" | "general">("builtin");

  // Onboarding
  const [showOnboarding, setShowOnboarding] = useState(false);
  const [onboardingChecked, setOnboardingChecked] = useState(false);
  const [onboardingCheckError, setOnboardingCheckError] = useState("");
  const [activeSessionId, setActiveSessionId] = useState<number | null>(null);

  const checkOnboarding = useCallback(() => {
    setOnboardingChecked(false);
    setOnboardingCheckError("");
    dotnetRequest<{ isFirstRun: boolean }>("settings", "checkFirstRun")
      .then(({ isFirstRun }) => {
        setShowOnboarding(isFirstRun);
        setOnboardingChecked(true);
      })
      .catch((error) => {
        setOnboardingCheckError(error instanceof Error ? error.message : String(error));
        setOnboardingChecked(true);
      });
  }, []);

  useEffect(() => {
    checkOnboarding();
  }, [checkOnboarding]);

  useEffect(() => {
    if (typeof window === "undefined" || !("__TAURI_INTERNALS__" in window)) return;
    let cleanup: (() => void) | undefined;
    listen<TranslationError>("translation-provider-error", (event) => setTranslationError(event.payload)).then((unlisten) => { cleanup = unlisten; });
    return () => cleanup?.();
  }, []);

  function openSettings(tab: "builtin" | "llm" | "general") {
    setSettingsTab(tab); setSettingsOpen(true); setTranslationError(null);
  }

  // Timer de sesión
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  useEffect(() => {
    if (isSessionActive && sessionStartTime) {
      timerRef.current = setInterval(() => {
        const diff = Math.floor((Date.now() - sessionStartTime.getTime()) / 1000);
        const h = String(Math.floor(diff / 3600)).padStart(2, "0");
        const m = String(Math.floor((diff % 3600) / 60)).padStart(2, "0");
        const s = String(diff % 60).padStart(2, "0");
        setSessionDuration(`${h}:${m}:${s}`);
      }, 1000);
    }
    return () => { if (timerRef.current) clearInterval(timerRef.current); };
  }, [isSessionActive, sessionStartTime]);

  // Animar barras de audio cuando la sesión está activa
  useEffect(() => {
    if (isSessionActive) {
      const interval = setInterval(() => {
        setBarHeights([
          Math.random() * 20 + 10 + "px",
          Math.random() * 20 + 10 + "px",
          Math.random() * 20 + 10 + "px",
        ]);
      }, 300);
      return () => clearInterval(interval);
    } else {
      setBarHeights(["12px", "20px", "12px"]);
    }
  }, [isSessionActive]);

  const lineIdRef = useRef(0);

  // Eventos Tauri desde C#
  useCaptionEvents({
    onTranscriptionLine: useCallback((payload: TranscriptionLine) => {
      const line = { ...payload, sequenceId: lineIdRef.current++ };
      setTranscriptionLines((prev) => [...prev.slice(-200), line]);
      setPartialText("");
    }, []),
    onTranscriptionPartial: useCallback((payload: { text: string; timestamp: string }) => {
      setPartialText(payload.text);
    }, []),
    onTranslationReady: useCallback((payload: TranslationLine) => {
      setTranslationLines((prev) => [...prev.slice(-200), payload]);
    }, []),
    onQuestionDetected: useCallback((payload: DetectedQuestion) => {
      const id = `question-${Date.now()}-${Math.random().toString(36).slice(2)}`;
      setQuestionItems((prev) => [...prev, { ...payload, id, answer: "", status: "processing" as const }].slice(-50));
      setQuestionCount((n) => n + 1);
    }, []),
    onAnswerChunk: useCallback((payload: { chunk: string }) => {
      setQuestionItems((prev) => {
        const reverseIndex = [...prev].reverse().findIndex((item) => item.status === "processing");
        if (reverseIndex < 0) return prev;
        const target = prev.length - 1 - reverseIndex;
        return prev.map((item, index) => index === target ? { ...item, answer: item.answer + payload.chunk } : item);
      });
    }, []),
    onAnswerComplete: useCallback((payload: { answer: string }) => {
      setQuestionItems((prev) => {
        const reverseIndex = [...prev].reverse().findIndex((item) => item.status === "processing");
        if (reverseIndex < 0) return prev;
        const target = prev.length - 1 - reverseIndex;
        return prev.map((item, index) => index === target ? { ...item, answer: payload.answer, status: "answered" } : item);
      });
    }, []),  });

  async function handleStart() {
    if (isStarting || isSessionActive) return;
    setIsStarting(true);
    try {
      const startResult = await dotnetRequest<{ sessionId?: number }>("session", "start");
      setActiveSessionId(startResult?.sessionId ?? null);
      setIsSessionActive(true);
      setSessionStartTime(new Date());
      lineIdRef.current = 0;
      setTranscriptionLines([]);
      setTranslationLines([]);
      setQuestionItems([]);
      setQuestionCount(0);
      setSessionDuration("00:00:00");
    } catch (err) {
      toast.error(`Error al iniciar sesión: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setIsStarting(false);
    }
  }

  async function handleStop() {
    try {
      await dotnetRequest("session", "stop");
    } catch (err) {
      toast.error(`Error al detener sesión: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setIsSessionActive(false);
      if (timerRef.current) clearInterval(timerRef.current);
    }
  }

  // No renderizar hasta saber si hay que mostrar onboarding
  if (!onboardingChecked) {
    return (
      <div className="flex h-screen items-center justify-center bg-gray-50 px-6 text-center">
        <div>
          <div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-gray-300 border-t-gray-900" />
          <p className="mt-3 text-sm text-gray-600">Preparando la aplicación…</p>
        </div>
      </div>
    );
  }

  if (onboardingCheckError) {
    return (
      <div className="flex h-screen items-center justify-center bg-gray-50 px-6">
        <div role="alert" className="w-full max-w-md rounded-2xl border border-red-200 bg-white p-6 text-center shadow-sm">
          <AlertTriangle className="mx-auto text-red-600" />
          <h1 className="mt-3 text-lg font-semibold text-gray-950">No se pudo iniciar la configuración</h1>
          <p className="mt-2 break-words text-sm text-gray-600">{onboardingCheckError}</p>
          <button type="button" onClick={checkOnboarding} className="mt-5 inline-flex min-h-10 items-center gap-2 rounded-xl bg-gray-950 px-4 text-sm font-medium text-white hover:bg-gray-800">
            <RefreshCw size={14} />
            Reintentar
          </button>
        </div>
      </div>
    );
  }

  if (showOnboarding) {
    return <OnboardingFlow onComplete={() => setShowOnboarding(false)} />;
  }

  return (
    <>

{/* App principal */}
      <div className="flex h-screen bg-gray-50 overflow-hidden">
        {/* Sidebar de historial */}
        <Sidebar
          activeSessionId={activeSessionId}
          onSelectSession={(id) => setActiveSessionId(id)}
          onNewSession={() => {
            handleStop();
            setActiveSessionId(null);
            setTranscriptionLines([]);
            setTranslationLines([]);
            setQuestionItems([]);
          }}
        />

        {/* Contenido principal */}
        <div className="flex flex-col flex-1 overflow-hidden">
          <TitleBar
            sessionActive={isSessionActive}
            sessionDuration={sessionDuration}
            questionCount={questionCount}
          />

          {/* Paneles de transcripción y traducción */}
          <div className="flex flex-1 min-w-0 flex-col overflow-hidden divide-y divide-border pb-28 md:flex-row md:divide-x md:divide-y-0">
            <div className="h-1/2 min-h-0 w-full overflow-hidden bg-white md:h-auto md:w-1/2">
              <TranscriptionPanel lines={transcriptionLines} partial={partialText} />
            </div>
            <div className="h-1/2 min-h-0 w-full overflow-hidden bg-white md:h-auto md:w-1/2">
              <AnimatePresence>
                {translationError && <motion.div initial={{ opacity: 0, y: -12 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -12 }} className="m-3 flex items-start gap-2 rounded-lg border border-amber-300/50 bg-amber-50 p-3 text-amber-950" role="alert">
                  <AlertTriangle size={16} className="mt-0.5 shrink-0 text-amber-600" />
                  <div className="min-w-0 flex-1">
                    <p className="text-xs font-semibold">{translationError.reason === "builtin_no_model" ? "El modelo local integrado no está descargado" : translationError.reason === "lmstudio_offline" ? "El servidor de LM Studio está offline (puerto 1234)" : "No hay un proveedor de traducción disponible"}</p>
                    {translationError.details && <p className="mt-1 text-[11px] text-amber-900/70">{translationError.details}</p>}
                    <button onClick={() => openSettings(translationError.reason === "builtin_no_model" ? "builtin" : "llm")} className="mt-2 inline-flex items-center gap-1 text-[11px] font-semibold text-amber-800">{translationError.reason === "builtin_no_model" ? <Download size={12} /> : <Settings2 size={12} />}{translationError.reason === "builtin_no_model" ? "Descargar Modelo Integrado" : "Cambiar Proveedor"}<ArrowRight size={12} /></button>
                  </div>
                  <button onClick={() => setTranslationError(null)} aria-label="Cerrar aviso" className="text-amber-700"><X size={14} /></button>
                </motion.div>}
              </AnimatePresence>
              <TranslationPanel lines={translationLines} />
            </div>
          </div>

          {/* Panel de preguntas y respuestas */}
          <div className="relative z-20 shrink-0 pb-24">
            <QuestionPanel items={questionItems} />
          </div>
        </div>
      </div>

      {/* Controles flotantes (píldora Meetily-style) */}
      <SessionControls
        isActive={isSessionActive}
        barHeights={barHeights}
        onStart={handleStart}
        onStop={handleStop}
        onSettings={() => setSettingsOpen(true)}
        isStarting={isStarting}
        currentSessionId={activeSessionId ?? undefined}
      />

      {/* Panel de configuración (overlay) */}
      <SettingsPanel open={settingsOpen} tab={settingsTab} onClose={() => setSettingsOpen(false)} />
    </>
  );
}
