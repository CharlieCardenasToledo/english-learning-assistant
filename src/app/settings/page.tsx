"use client";

import { useRouter } from "next/navigation";
import { Sidebar } from "@/components/Sidebar/Sidebar";
import { SettingsPanel } from "@/components/SettingsPanel/SettingsPanel";
import { useState } from "react";

export default function SettingsPage() {
  const router = useRouter();
  // Mantener siempre open=true para que el panel no sea un modal
  // pero necesita onClose para cerrar el diálogo si el usuario navega
  const [open] = useState(true);

  return (
    <div className="flex h-screen bg-gray-50 overflow-hidden">
      <Sidebar onNewSession={() => router.push("/")} />

      <main className="flex-1 overflow-y-auto">
        {/* Reutiliza SettingsPanel completo. open=true y onClose navega al inicio */}
        <SettingsPanel open={open} onClose={() => router.push("/")} />
      </main>
    </div>
  );
}
