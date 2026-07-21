/** @type {import('next').NextConfig} */
const nextConfig = {
  output: "export",
  // Tauri espera archivos estáticos — imágenes no optimizadas
  images: { unoptimized: true },
  // Evita conflictos con rutas de assets en Tauri
  assetPrefix: process.env.NODE_ENV === "production" ? "" : undefined,
};

module.exports = nextConfig;
