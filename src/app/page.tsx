"use client";

import { useState, useCallback, useEffect, useRef } from "react";
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
import type { TranscriptionLine, TranslationLine, DetectedQuestion } from "@/types";

export default function HomePage() {
  const [transcriptionLines, setTranscriptionLines] = useState<TranscriptionLine[]>([]);
  const [partialText, setPartialText] = useState("");
  const [translationLines, setTranslationLines] = useState<TranslationLine[]>([]);
  const [currentQuestion, setCurrentQuestion] = useState<DetectedQuestion | null>(null);
  const [answer, setAnswer] = useState("");
  const [isStreaming, setIsStreaming] = useState(false);
  const [isSessionActive, setIsSessionActive] = useState(false);
  const [sessionStartTime, setSessionStartTime] = useState<Date | null>(null);
  const [sessionDuration, setSessionDuration] = useState("00:00:00");
  const [questionCount, setQuestionCount] = useState(0);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [barHeights, setBarHeights] = useState(["12px", "20px", "12px"]);

  // Onboarding
  const [showOnboarding, setShowOnboarding] = useState(false);
  const [onboardingChecked, setOnboardingChecked] = useState(false);
  const [activeSessionId, setActiveSessionId] = useState<number | null>(null);

  useEffect(() => {
    dotnetRequest<{ isFirstRun: boolean }>("settings", "checkFirstRun")
      .then(({ isFirstRun }) => {
        setShowOnboarding(isFirstRun);
        setOnboardingChecked(true);
      })
      .catch(() => setOnboardingChecked(true));
  }, []);

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

  // Eventos Tauri desde C#
  useCaptionEvents({
    onTranscriptionLine: useCallback((payload: TranscriptionLine) => {
      setTranscriptionLines((prev) => [...prev.slice(-200), payload]);
      setPartialText("");
    }, []),
    onTranscriptionPartial: useCallback((payload: { text: string; timestamp: string }) => {
      setPartialText(payload.text);
    }, []),
    onTranslationReady: useCallback((payload: TranslationLine) => {
      setTranslationLines((prev) => [...prev.slice(-200), payload]);
    }, []),
    onQuestionDetected: useCallback((payload: DetectedQuestion) => {
      setCurrentQuestion(payload);
      setAnswer("");
      setIsStreaming(true);
      setQuestionCount((n) => n + 1);
    }, []),
    onAnswerChunk: useCallback((payload: { chunk: string }) => {
      setAnswer((prev) => prev + payload.chunk);
    }, []),
    onAnswerComplete: useCallback((payload: { answer: string }) => {
      setAnswer(payload.answer);
      setIsStreaming(false);
    }, []),
  });

  function handleStart() {
    setIsSessionActive(true);
    setSessionStartTime(new Date());
    setTranscriptionLines([]);
    setTranslationLines([]);
    setCurrentQuestion(null);
    setAnswer("");
    setQuestionCount(0);
    setSessionDuration("00:00:00");
  }

  function handleStop() {
    setIsSessionActive(false);
    if (timerRef.current) clearInterval(timerRef.current);
  }

  // No renderizar hasta saber si hay que mostrar onboarding
  if (!onboardingChecked) return null;

  return (
    <>
      {/* Onboarding (cubre toda la pantalla si es primera vez) */}
      {showOnboarding && (
        <OnboardingFlow onComplete={() => setShowOnboarding(false)} />
      )}

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
            setCurrentQuestion(null);
            setAnswer("");
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
          <div className="flex flex-1 overflow-hidden divide-x divide-border pb-20">
            <div className="w-1/2 overflow-hidden bg-white">
              <TranscriptionPanel lines={transcriptionLines} partial={partialText} />
            </div>
            <div className="w-1/2 overflow-hidden bg-white">
              <TranslationPanel lines={translationLines} />
            </div>
          </div>

          {/* Panel de preguntas y respuestas */}
          <QuestionPanel question={currentQuestion} answer={answer} isStreaming={isStreaming} />
        </div>
      </div>

      {/* Controles flotantes (píldora Meetily-style) */}
      <SessionControls
        isActive={isSessionActive}
        barHeights={barHeights}
        onStart={handleStart}
        onStop={handleStop}
        onSettings={() => setSettingsOpen(true)}
      />

      {/* Panel de configuración (overlay) */}
      <SettingsPanel open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </>
  );
}
