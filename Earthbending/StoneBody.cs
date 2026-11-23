using System;
using Alta.Networking;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    /// <summary>The server's side of one standing stone (spec 3.2: the server owns every stone), added by Server.Spawn to the
    /// spawned copy on the server only; games never have one. It knows the stone's kind, owner, state and age. While Rising it
    /// moves the kinematic rigidbody from the start to the end over the rise time (Rigidbody.MovePosition each physics step;
    /// NetworkRigidbody carries the place and the kinematic flag to the games), then it is Still. <see cref="Break"/> is the one
    /// way a stone is ended on purpose.</summary>
    internal sealed class StoneBody : MonoBehaviour {
        internal StoneKind Kind;
        /// <summary>The owner's account (Server.AccountOf); 0 for a stone nobody owns.</summary>
        internal ulong Owner;
        internal string OwnerName = "";
        internal StoneState State;
        /// <summary>When it was spawned (Time.unscaledTimeAsDouble on the server).</summary>
        internal double Born;
        internal uint Id;
        internal NetworkEntity? Entity;

        Rigidbody? body;
        Vector3 start, end;
        float riseSeconds, risen;
        bool broken;
        static bool sendFailed, destroyFailed;

        internal void Init(NetworkEntity entity, StoneKind kind, ulong owner, string ownerName, StoneState state, Vector3 from, Vector3 to, float rise, double born) {
            Entity = entity;
            Id = entity.Identifier;
            Kind = kind;
            Owner = owner;
            OwnerName = ownerName;
            State = state;
            start = from;
            end = to;
            riseSeconds = rise;
            risen = 0f;
            Born = born;
            body = GetComponent<Rigidbody>();
        }

        void FixedUpdate() {
            if (broken || State != StoneState.Rising) return;
            risen += Time.fixedDeltaTime;
            float t = riseSeconds > 0f ? Mathf.Clamp01(risen / riseSeconds) : 1f;
            Vector3 at = Vector3.LerpUnclamped(start, end, t);
            if (body != null) body.MovePosition(at);
            else transform.position = at;
            if (t < 1f) return;
            State = StoneState.Still;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} has risen to {end} and stands still");
        }

        /// <summary>Server: the stone breaks. The games within 80 m get Applied(Broke) at its place (they play the break), then
        /// the entity is destroyed. One Debug line with <paramref name="why"/>. Twice is once.</summary>
        internal void Break(string why) {
            if (broken) return;
            broken = true;
            Server.Forget(this);
            Vector3 at = transform.position;
            try { Net.SendApplied(Id, Wire.Broke, at); }
            catch (Exception e) {
                if (!sendFailed) { sendFailed = true; MelonLogger.Warning($"[Earth] Stones: telling the games a stone broke failed (said once): {e.Message}"); }
            }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} broke at {at}: {why}");
            try {
                if (Entity != null && !Entity.IsBeingDestroyed) Entity.SceneDestroy();
            }
            catch (Exception e) {
                if (!destroyFailed) { destroyFailed = true; MelonLogger.Warning($"[Earth] Stones: destroying a broken stone failed (said once): {e.Message}"); }
            }
        }

        // destroyed by anything (Break, `earth clear`, its chunk unloading): it no longer stands
        void OnDestroy() {
            broken = true;
            Server.Forget(this);
        }
    }
}
