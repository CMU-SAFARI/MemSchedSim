using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace MemMap
{
    public class MetaMemCtrl
    {
        public bool is_omniscient;
        public MemCtrl mctrl;
        public MemCtrl[] mctrls;
        public MemSched sched;
        public MemSched wbsched;
        public List<Bank> banks;
        public int prev_req_pid;

        //non-omniscient
        public MetaMemCtrl(MemCtrl mctrl, MemSched sched, MemSched wbsched)
        {
            is_omniscient = false;
            this.mctrl = mctrl;
            this.mctrls = new MemCtrl[] { mctrl };
            this.sched = sched;
            this.wbsched = wbsched;
            set_banks();
        }

        //omniscient
        public MetaMemCtrl(MemCtrl[] mctrls, MemSched sched, MemSched wbsched)
        {
            is_omniscient = true;
            this.mctrl = null;
            this.mctrls = mctrls;
            this.sched = sched;
            this.wbsched = wbsched;

            set_banks();
        }

        private void set_banks()
        {
            banks = new List<Bank>();
            foreach (MemCtrl mc in this.mctrls) {
                Channel chan = mc.chan;
                for (uint r = 0; r < chan.rmax; r++) {
                    Rank rank = chan.ranks[r];
                    for (uint b = 0; b < rank.bmax; b++) {
                        Bank bank = rank.banks[b];
                        banks.Add(bank);
                    }
                }
            }
        }

        public void issue_req(Req req)
        {
            sched.issue_req(req);
            wbsched.issue_req(req);
        }

        public void enqueue_req(Req req)
        {
            sched.enqueue_req(req);
            wbsched.issue_req(req);
        }

        public void dequeue_req(Req req)
        {
            sched.dequeue_req(req);
            wbsched.issue_req(req);
        }

        public bool is_row_hit(Req req)
        {
            MemCtrl mctrl = get_mctrl(req);

            Bank bank = mctrl.chan.ranks[req.addr.rid].banks[req.addr.bid];
            return bank.curr_rowid == (long)req.addr.rowid;
        }

        public Req find_best_rd_req(List<Req> q)
        {
            return sched.find_best_req(q);
        }

        public Req find_best_wb_req(List<Req> wq)
        {
            return wbsched.find_best_req(wq);
        }

        public Req better_req(Req req1, Req req2)
        {
            return sched.better_req(req1, req2);
        }

        public Req better_wb_req(Req req1, Req req2)
        {
            return wbsched.better_req(req1, req2);

        }

        public void tick(uint cid)
        {
            /* non-omniscient */
            if (!is_omniscient) {
                sched.tick();
                if (!Config.sched.same_sched_algo) {
                    wbsched.tick();
                }
                return;
            }

            /* omniscient */
            if (cid == 0) {
                sched.tick();
                if (!Config.sched.same_sched_algo) {
                    wbsched.tick();
                }
            }
        }

        public long get_cycles()
        {
            if (!is_omniscient) {
                return mctrl.cycles;
            }

            return mctrls[0].cycles;
        }

        public Req get_curr_req(Bank bank)
        {
            MemCtrl mc = bank.mc;
            List<Req> inflight_q = mc.inflightqs[bank.rid, bank.bid];
            if (inflight_q.Count == 0)
                return null;

            return inflight_q[inflight_q.Count - 1];
        }

        public MemCtrl get_mctrl(Req req)
        {
            if (!is_omniscient) {
                Dbg.Assert(mctrl.cid == req.addr.cid);
                return mctrl;
            }
            return mctrls[req.addr.cid];
        }

        public MemCtrl get_mctrl(Bank bank)
        {
            if (!is_omniscient) {
                Dbg.Assert(mctrl.cid == bank.cid);
                return mctrl;
            }
            return mctrls[bank.cid];
        }

        public uint get_bmax()
        {
            return (uint) banks.Count;
        }

        public uint get_bid(Req req)
        {
            uint cid = req.addr.cid;
            uint rid = req.addr.rid;
            uint bid = req.addr.bid;

            uint global_bid = 0;
            if (is_omniscient && cid > 0) {
                global_bid += (cid - 1) * mctrls[0].rmax * mctrls[0].bmax;
            }
            if(rid > 0){
                global_bid += (rid - 1) * mctrls[0].bmax;
            }
            global_bid += bid;
            return global_bid;
        }

        public uint get_rload()
        {
            return get_load(true);
        }

        public uint get_wload()
        {
            return get_load(false);
        }

        private uint get_load(bool read)
        {
            if (!is_omniscient) {
                if(read) return mctrl.rload;
                return mctrl.wload;
            }

            uint load = 0;
            foreach (MemCtrl mc in mctrls) {
                if (read) load += mc.rload;
                else load += mc.wload;
            }
            return load;
        }

        public bool is_writeq_full()
        {
            return get_writeq_max() == get_wload();
        }

        public uint get_writeq_max()
        {
            if (!is_omniscient) return (uint) mctrl.mctrl_writeq.Capacity;

            uint writeq_max = 0;
            foreach (MemCtrl mc in mctrls) {
                writeq_max += (uint) mc.mctrl_writeq.Capacity;
            }
            return writeq_max;
        }

        public bool is_req_to_cur_proc(Req req)
        {
            MemCtrl mctrl = get_mctrl(req);

            Bank bank = mctrl.chan.ranks[req.addr.rid].banks[req.addr.bid];
            Req curr_req = get_curr_req(bank);

            if (curr_req != null) return curr_req.pid == (long)req.pid;
            else return false;
        }


        public uint get_load_per_proc(uint pid)
        {
            if (!is_omniscient)
                return mctrl.rload_per_proc[pid];

            uint load = 0;
            foreach (MemCtrl m in mctrls) {
                load += m.rload_per_proc[pid];
            }
            return load;
        }

        public uint get_load_per_procbank(uint pid, uint bid)
        {
            uint banks_per_mctrl = mctrls[0].rmax * mctrls[0].bmax;
            uint banks_per_rank = mctrls[0].bmax;

            uint rid = (bid % banks_per_mctrl) / banks_per_rank;
            uint local_bid = bid % mctrls[0].bmax;

            if (!is_omniscient) {
                return mctrl.rload_per_procrankbank[pid, rid, local_bid];
            }

            uint mid = bid / banks_per_mctrl;
            return mctrls[(int) mid].rload_per_procrankbank[pid, rid, local_bid];
        }

        public List<Req> get_readq(Bank bank)
        {
            MemCtrl mc = get_mctrl(bank);
            return mc.readqs[bank.rid, bank.bid];
        }

        public List<Req> get_writeq(Bank bank)
        {
            MemCtrl mc = get_mctrl(bank);
            return mc.writeqs[bank.rid, bank.bid];
        }
    }
}
