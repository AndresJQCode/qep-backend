namespace Api;

public static class RateLimiterPolicies
{
    // Compartido por las superficies públicas/sin autenticar: ventana fija por IP, generosa
    // para tráfico real pero acotada contra el abuso.
    public const string Public = "public";

    // Los endpoints previos a la sesión: el login (/auth/session) y el auto-registro de tenant
    // (/auth/register-tenant). Ventana fija de 10 por minuto por IP del cliente, aparte de
    // Public porque es otro presupuesto: una persona real llama al login una vez por sesión
    // —que dura hasta 30 días— y a register-tenant una sola vez, así que 10 dejan margen para
    // reintentos y para varias personas detrás del mismo NAT de una oficina, y a la vez acotan
    // el token stuffing, la fuerza bruta y el costo del camino de aprovisionamiento. Los dos
    // endpoints comparten el bucket, a propósito: la partición es sólo la IP, así que quien
    // martilla register-tenant también gasta su presupuesto de login.
    public const string Authentication = "authentication";
}
