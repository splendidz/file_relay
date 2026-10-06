using System;
using System.Collections.Generic;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>공유 폴더 권한 단계. 높은 단계는 낮은 단계 권한을 포함한다.</summary>
    public enum AccessLevel
    {
        None = 0,   // 보이지 않음
        Read = 1,   // 목록 + 내려받기
        Write = 2,  // + 올리기(새 파일), 새 폴더, 이어올리기 — 삭제/덮어쓰기/이름변경 불가 ("복사만")
        Full = 3,   // + 삭제, 이름 변경, 덮어쓰기
    }

    /// <summary>접속 상태.</summary>
    public enum Presence { Offline = 0, Online = 1, Away = 2, Busy = 3 }

    /// <summary>피어 관계 상태.</summary>
    public enum PeerState
    {
        Paired = 0,     // 서로 등록됨 (정상)
        Pending = 1,    // 내가 추가했으나 상대가 아직 응답/수락하지 않음
        Incoming = 2,   // 상대가 나를 추가 요청 — 내 수락 대기 (자동 수락 꺼짐일 때)
        Unpaired = 3,   // 상대가 나를 목록에서 삭제함
    }

    public static class AccessParse
    {
        public static AccessLevel Parse(string s, AccessLevel def = AccessLevel.Read)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "none": case "hidden": case "deny": case "없음": return AccessLevel.None;
                case "read": case "readonly": case "ro": case "읽기": return AccessLevel.Read;
                case "write": case "readwrite": case "rw": case "copy": case "쓰기": return AccessLevel.Write;
                case "full": case "all": case "admin": case "delete": case "전체": return AccessLevel.Full;
            }
            return def;
        }

        public static string ToKey(AccessLevel a)
        {
            switch (a)
            {
                case AccessLevel.None: return "none";
                case AccessLevel.Write: return "write";
                case AccessLevel.Full: return "full";
                default: return "read";
            }
        }

        public static Presence ParsePresence(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "online": return Presence.Online;
                case "away": return Presence.Away;
                case "busy": return Presence.Busy;
                default: return Presence.Offline;
            }
        }

        public static string PresenceKey(Presence p) => p.ToString().ToLowerInvariant();
    }

    /// <summary>피어 (다른 PC 의 FileRelay).</summary>
    public sealed class Peer
    {
        // ── 저장되는 값 ──
        public string Id = "";          // 상대 인스턴스 ID (상대가 한 번도 응답하지 않았으면 빈 값)
        public string Name = "";        // 상대가 등록한 이름
        public string Alias = "";       // 내가 붙인 별칭 (선택)
        public string Host = "";        // IP 또는 호스트 이름
        public int Port;
        public PeerState State = PeerState.Pending;
        public bool Blocked;
        public DateTime AddedAt = DateTime.Now;
        public DateTime LastSeen = DateTime.MinValue;

        // ── 실행 중에만 쓰는 값 ──
        public volatile Presence Presence = Presence.Offline;
        public DateTime LastInbound = DateTime.MinValue;   // 상대가 나에게 요청한 마지막 시각
        public DateTime LastOutboundOk = DateTime.MinValue; // 내가 상대에게 성공한 마지막 시각
        public int FailCount;
        public string Version = "";
        public string Machine = "";
        public int ShareCount = -1;
        public string LastError = "";
        public int Unread;          // 안 읽은 쪽지 수 (UI 표시용)

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Alias) ? Alias :
            !string.IsNullOrWhiteSpace(Name) ? Name : Host;

        public string Address => (Host ?? "") + ":" + Port;

        public bool IsOnline => Presence != Presence.Offline;

        public bool CanUse => IsOnline && State == PeerState.Paired && !Blocked;

        public JObj ToJson() => new JObj
        {
            ["id"] = Id,
            ["name"] = Name,
            ["alias"] = Alias,
            ["host"] = Host,
            ["port"] = Port,
            ["state"] = State.ToString().ToLowerInvariant(),
            ["blocked"] = Blocked,
            ["addedAt"] = AddedAt,
            ["lastSeen"] = LastSeen,
        };

        public static Peer FromJson(JObj o)
        {
            var p = new Peer
            {
                Id = o.S("id"),
                Name = o.S("name"),
                Alias = o.S("alias"),
                Host = o.S("host"),
                Port = o.I("port", AppConfig.DefaultPort),
                Blocked = o.B("blocked"),
                AddedAt = TimeUtil.ParseIso(o.S("addedAt")),
                LastSeen = TimeUtil.ParseIso(o.S("lastSeen")),
            };
            Enum.TryParse(o.S("state", "pending"), true, out p.State);
            return p;
        }

        public override string ToString() => DisplayName;
    }

    public enum FsKind { File, Dir, Drive, Share, Special }

    /// <summary>파일 목록의 한 항목 (로컬/원격 공통).</summary>
    public sealed class FsItem
    {
        public string Name = "";
        public string Path = "";        // 해당 소스의 경로 체계 (로컬: 전체 경로, 원격: share\sub\name)
        public FsKind Kind;
        public long Size;
        public DateTime MTime;
        public bool Hidden;
        public AccessLevel Access;      // 공유(Share) 항목의 권한
        public long Free = -1, Total = -1;
        public string Info = "";        // '종류' 열에 덧붙일 설명 (드라이브 이름, 공유 설명 등)

        public bool IsDir => Kind != FsKind.File;
        public bool IsContainerRoot => Kind == FsKind.Drive || Kind == FsKind.Share || Kind == FsKind.Special;

        public override string ToString() => Name;
    }

    /// <summary>쪽지 한 통.</summary>
    public sealed class Note
    {
        public string Id = Guid.NewGuid().ToString("N");
        public bool Incoming;
        public string PeerId = "";
        public string PeerName = "";
        public string Text = "";
        public string Color = "yellow";
        public DateTime Time = DateTime.Now;
        public DateTime ReadAt = DateTime.MinValue;     // 받은 쪽지: 내가 읽은 시각 / 보낸 쪽지: 상대가 읽은 시각
        public string ReplyTo = "";
        public bool Deleted;

        public bool IsRead => ReadAt > DateTime.MinValue.AddDays(1);

        public JObj ToJson() => new JObj
        {
            ["id"] = Id,
            ["in"] = Incoming,
            ["peerId"] = PeerId,
            ["peerName"] = PeerName,
            ["text"] = Text,
            ["color"] = Color,
            ["time"] = Time,
            ["readAt"] = ReadAt,
            ["replyTo"] = ReplyTo,
        };

        public static Note FromJson(JObj o) => new Note
        {
            Id = o.S("id", Guid.NewGuid().ToString("N")),
            Incoming = o.B("in"),
            PeerId = o.S("peerId"),
            PeerName = o.S("peerName"),
            Text = o.S("text"),
            Color = o.S("color", "yellow"),
            Time = TimeUtil.ParseIso(o.S("time")),
            ReadAt = TimeUtil.ParseIso(o.S("readAt")),
            ReplyTo = o.S("replyTo"),
        };
    }

    /// <summary>내 공유에 들어온 요청 기록 (공유 활동 탭).</summary>
    public sealed class ActivityEntry
    {
        public int Id;
        public DateTime Time = DateTime.Now;
        public string PeerName = "";
        public string Kind = "";        // download | upload | delete (색 구분용, 언어와 무관)
        public string Action = "";      // 화면에 보이는 작업 이름 (현재 언어)
        public string Path = "";
        public long Size = -1;
        public long Done;
        public double Speed;
        public string Status = "";
        public bool Finished;
        public bool Failed;
    }
}
