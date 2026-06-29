// Chart.js interop for Copilot Session Inspector.
window.copilotCharts = (function () {
    const charts = {};

    function render(canvasId, config) {
        // Retry briefly in case Chart.js or the canvas isn't ready yet.
        function attempt(triesLeft) {
            const el = document.getElementById(canvasId);
            if (typeof Chart === 'undefined' || !el) {
                if (triesLeft > 0) {
                    setTimeout(() => attempt(triesLeft - 1), 100);
                } else {
                    console.warn('copilotCharts: Chart.js or canvas not available for', canvasId);
                }
                return;
            }
            if (charts[canvasId]) {
                charts[canvasId].destroy();
            }
            charts[canvasId] = new Chart(el.getContext('2d'), config);
        }
        attempt(20);
    }

    return {
        renderTokensPerTurn: function (canvasId, labels, input, output, reasoning) {
            render(canvasId, {
                type: 'bar',
                data: {
                    labels: labels,
                    datasets: [
                        { label: 'Input', data: input, backgroundColor: '#4e79a7' },
                        { label: 'Output', data: output, backgroundColor: '#59a14f' },
                        { label: 'Reasoning', data: reasoning, backgroundColor: '#e15759' }
                    ]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    scales: {
                        x: { stacked: true, title: { display: true, text: 'Turn' } },
                        y: { stacked: true, title: { display: true, text: 'Tokens' } }
                    }
                }
            });
        },

        renderCumulative: function (canvasId, labels, aiu, durationSec) {
            render(canvasId, {
                type: 'line',
                data: {
                    labels: labels,
                    datasets: [
                        { label: 'Cumulative AIU', data: aiu, borderColor: '#b07aa1', backgroundColor: '#b07aa1', yAxisID: 'y', tension: 0.2 },
                        { label: 'Cumulative time (s)', data: durationSec, borderColor: '#f28e2b', backgroundColor: '#f28e2b', yAxisID: 'y1', tension: 0.2 }
                    ]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    interaction: { mode: 'index', intersect: false },
                    scales: {
                        x: { title: { display: true, text: 'Turn' } },
                        y: { type: 'linear', position: 'left', title: { display: true, text: 'AIU' } },
                        y1: { type: 'linear', position: 'right', title: { display: true, text: 'Seconds' }, grid: { drawOnChartArea: false } }
                    }
                }
            });
        },

        renderContext: function (canvasId, labels, current, limit) {
            render(canvasId, {
                type: 'line',
                data: {
                    labels: labels,
                    datasets: [
                        { label: 'Context tokens', data: current, borderColor: '#4e79a7', backgroundColor: 'rgba(78,121,167,0.2)', fill: true, tension: 0.2 },
                        { label: 'Token limit', data: limit, borderColor: '#e15759', borderDash: [6, 6], pointRadius: 0 }
                    ]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    scales: {
                        x: { title: { display: true, text: 'Sample #' } },
                        y: { title: { display: true, text: 'Tokens' }, beginAtZero: true }
                    }
                }
            });
        }
    };
})();

// Small UI helpers for the session detail page.
window.copilotUi = {
    setAllTurns: function (open) {
        document.querySelectorAll('details.turn-card').forEach(function (d) {
            d.open = open;
        });
    },

    scrollToTop: function () {
        window.scrollTo({ top: 0, behavior: 'smooth' });
    }
};
