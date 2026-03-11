module.exports = {
  content: [
    "./wwwroot/**/*.html",
    "./wwwroot/**/*.js"
  ],
  theme: {
    extend: {
      fontFamily: {
        display: ["Bahnschrift", "Trebuchet MS", "sans-serif"],
        sans: ["Aptos", "Segoe UI", "sans-serif"]
      },
      colors: {
        abyss: "#041319",
        tide: "#0b2730",
        seafoam: "#7dd3c7",
        ember: "#f59e0b",
        coral: "#f97316",
        slateglass: "#0f172a"
      },
      boxShadow: {
        panel: "0 24px 80px rgba(3, 12, 19, 0.42)"
      },
      keyframes: {
        drift: {
          "0%, 100%": { transform: "translate3d(0, 0, 0) scale(1)" },
          "50%": { transform: "translate3d(0, 24px, 0) scale(1.04)" }
        },
        lift: {
          "0%": { opacity: "0", transform: "translateY(18px)" },
          "100%": { opacity: "1", transform: "translateY(0)" }
        },
        pulseborder: {
          "0%, 100%": { borderColor: "rgba(125, 211, 199, 0.12)" },
          "50%": { borderColor: "rgba(125, 211, 199, 0.32)" }
        }
      },
      animation: {
        drift: "drift 18s ease-in-out infinite",
        lift: "lift 600ms ease-out both",
        pulseborder: "pulseborder 5s ease-in-out infinite"
      }
    }
  },
  plugins: []
};
