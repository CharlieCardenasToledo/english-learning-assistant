"use client";

import { useEffect, useRef } from "react";
import { motion, AnimatePresence } from "framer-motion";
import type { TranscriptionLine } from "@/types";

interface Props {
  lines: TranscriptionLine[];
  partial?: string;
}

export function TranscriptionPanel({ lines, partial }: Props) {
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [lines, partial]);

  return (
    <div className="flex flex-col h-full overflow-hidden">
      <div className="px-3 py-1.5 border-b border-border bg-muted/30">
        <span className="text-xs font-semibold text-muted-foreground uppercase tracking-wide">Transcripción</span>
      </div>
      <div className="flex-1 overflow-y-auto p-3 space-y-1 scrollbar-thin scrollbar-thumb-border">
        <AnimatePresence initial={false}>
          {lines.map((line) => (
            <motion.p
              key={line.sequenceId}
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.2 }}
              className="text-sm text-foreground leading-relaxed"
            >
              {line.text}
            </motion.p>
          ))}
        </AnimatePresence>
        {partial && (
          <p className="text-sm text-muted-foreground italic leading-relaxed">{partial}</p>
        )}
        <div ref={bottomRef} />
      </div>
    </div>
  );
}
