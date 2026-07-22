"use client";

import { useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { Check, Clipboard, ChevronDown, HelpCircle, Sparkles, Clock3 } from "lucide-react";
import { cn } from "@/lib/utils";
import type { QuestionConversationItem, QuestionLevel } from "@/types";
import { QUESTION_LEVEL_COLORS, QUESTION_LEVEL_LABELS } from "@/types";

interface Props { items: QuestionConversationItem[]; }

export function QuestionPanel({ items }: Props) {
  const [collapsed, setCollapsed] = useState(false);
  const [copiedId, setCopiedId] = useState<string | null>(null);

  async function copyAnswer(id: string, answer: string) {
    if (!answer) return;
    try {
      await navigator.clipboard.writeText(answer);
      setCopiedId(id);
      window.setTimeout(() => setCopiedId(null), 1500);
    } catch {
      // Clipboard may be unavailable in a non-secure webview.
    }
  }

  if (items.length === 0) return null;
  const pending = items.filter((item) => item.status === "queued" || item.status === "processing").length;

  return (
    <motion.section
      layout
      initial={{ opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      className="relative z-20 mx-3 mb-3 overflow-hidden rounded-2xl border border-white/70 bg-white/90 shadow-xl shadow-slate-300/30 backdrop-blur-xl"
      aria-label="Tutor IA"
    >
      <header className="flex items-center gap-2 border-b border-slate-200/70 px-4 py-2.5">
        <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-gradient-to-br from-violet-500 to-indigo-500 text-white shadow-sm"><Sparkles size={14} /></div>
        <div className="min-w-0 flex-1">
          <p className="text-[10px] font-semibold uppercase tracking-wider text-slate-500">Tutor IA</p>
          <p className="text-[10px] text-slate-500">
            {items.length} {items.length === 1 ? "pregunta" : "preguntas"}{pending > 0 ? ` · ${pending} pendientes` : ""}
          </p>
        </div>
        <button onClick={() => setCollapsed((value) => !value)} className="rounded-lg p-1.5 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500" title={collapsed ? "Mostrar tutor" : "Minimizar tutor"} aria-label={collapsed ? "Mostrar tutor" : "Minimizar tutor"}><ChevronDown size={15} className={cn("transition-transform", collapsed && "-rotate-90")} /></button>
      </header>
      <AnimatePresence initial={false}>
        {!collapsed && (
          <motion.div initial={{ height: 0, opacity: 0 }} animate={{ height: "auto", opacity: 1 }} exit={{ height: 0, opacity: 0 }} className="max-h-64 space-y-2 overflow-y-auto px-4 py-3 scrollbar-thin scrollbar-thumb-slate-300" aria-live="polite">
            {items.map((item) => {
              const level = QUESTION_LEVEL_LABELS[item.level as QuestionLevel] ?? "Tutor IA";
              const levelColor = QUESTION_LEVEL_COLORS[item.level as QuestionLevel] ?? "text-violet-600 border-violet-200 bg-violet-50";
              return (
                <article key={item.id} className="rounded-xl border border-slate-200/80 bg-white p-2.5">
                  <div className="flex items-start gap-2">
                    <HelpCircle size={15} className="mt-0.5 shrink-0 text-indigo-500" />
                    <div className="min-w-0 flex-1">
                      <div className="mb-1 flex items-center justify-between gap-2">
                        <span className={cn("inline-flex rounded-full border px-1.5 py-0.5 text-[10px] font-medium", levelColor)}>{level}</span>
                        {item.status === "processing" && <span className="flex items-center gap-1 text-[10px] text-indigo-500"><Sparkles size={11} className="animate-pulse" /> Respondiendo…</span>}
                        {item.status === "queued" && <span className="flex items-center gap-1 text-[10px] text-slate-400"><Clock3 size={11} /> En cola</span>}
                        {item.status === "failed" && <span className="text-[10px] text-red-500">No se pudo responder</span>}
                        {item.status === "answered" && <span className="text-[10px] text-green-600">Respondida</span>}
                      </div>
                      <p className="text-sm font-medium leading-relaxed text-slate-800">{item.text}</p>
                      {item.answer && <div className="mt-2 flex items-start gap-2 border-t border-slate-100 pt-2 text-sm leading-relaxed text-slate-600">{item.answer}<button onClick={() => copyAnswer(item.id, item.answer)} className="ml-auto shrink-0 rounded-lg p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500" title="Copiar respuesta" aria-label="Copiar respuesta">{copiedId === item.id ? <Check size={14} className="text-green-600" /> : <Clipboard size={14} />}</button></div>}
                    </div>
                  </div>
                </article>
              );
            })}
          </motion.div>
        )}
      </AnimatePresence>
    </motion.section>
  );
}