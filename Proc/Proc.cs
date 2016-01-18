using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Globalization;


using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.GZip;

namespace MemMap
{
    class Proc
    {
        public static readonly ulong NULL_ADDRESS = ulong.MaxValue;
        //throttle
        public static Random rand = new Random(0);
        public double throttle_fraction = 0;

        //processor id
        private static int pmax = 0;
        public int pid;

        //components
        public InstWnd inst_wnd;
        public List<ulong> mshr;
        public List<Req> wb_q;
        public Cache cache;
        public L1Cache l1_cache;

        //other components
        public Trace trace;


        //current status
        public ulong cycles;
        public int curr_cpu_inst_cnt;
        public ulong pc;
        public Req curr_rd_req;

        //retry memory request
        private bool mctrl_retry = false;
        private bool mshr_retry = false;

        //etc: outstanding requests
        public int out_read_req;

        //etc: stats
        ulong curr_quantum;
        ulong prev_read_req;
        ulong prev_write_req;

        private int prev_dump = 0;
        private ulong stall_shared_delta = 0;

        public ulong prev_inst_cnt = 0;
        public ulong prev_cycle = 0; 
        public ulong inst_cnt = 0;
        public LinkedList<Req> cache_hit_queue;
        public LinkedList<Req> mem_queue;
        public int quantum_cycles_left = Config.proc.quantum_cycles;

        public bool is_cache_hit = false;

        public bool is_in_cache = false;


        public int misses;
        public ulong [] criticality_table;
        public ulong [] criticality_running_table;
        public bool just_set_full = false;
        public bool set_full = true;
        public ulong full_address;

        ulong total_read_latency;
       
        public Proc(Cache cache, L1Cache l1_cache, string trace_fname)
        {
            pid = pmax;
            pmax++;

            //components
            inst_wnd = new InstWnd(Config.proc.inst_wnd_max);
            mshr = new List<ulong>(Config.proc.mshr_max);
            wb_q = new List<Req>(2 * Config.proc.wb_q_max);

            //other components
            Stat.procs[pid].trace_fname = trace_fname;
            trace = new Trace(pid, trace_fname);
            this.cache = cache;
            this.l1_cache = l1_cache;

            cache_hit_queue = new LinkedList<Req>();
            mem_queue = new LinkedList<Req>();
            criticality_table = new ulong[1024]; 
            criticality_running_table = new ulong[1024]; 

            //initialize
            curr_rd_req = get_req();
            total_read_latency = 0;            
        }



        public void recv_req(Req req)
        {
            //stats
            Stat.procs[pid].read_req_served.Collect();
            Stat.procs[pid].read_avg_latency.Collect(req.latency);
            total_read_latency += (ulong) req.latency;

            Req first_request = req;
            ulong wb_addr = Proc.NULL_ADDRESS;
            
            //free up instruction window and mshr
            inst_wnd.set_ready(req.block_addr);
            mshr.RemoveAll(x => x == req.block_addr);

            if (!cache.has_addr(first_request.block_addr, ReqType.RD))
            {
                wb_addr = cache.cache_add(first_request.block_addr, first_request.proc_req_type, (ulong)pid);
                if (!l1_cache.has_addr(first_request.block_addr, first_request.type)) 
                {
                    l1_cache.cache_add(first_request.block_addr, first_request.proc_req_type, (ulong)pid);
                }
                l1_cache.cache_remove(wb_addr, ReqType.RD);
            }

            //add to cache; returns the address of evicted block; returns null if empty block has been populated
            //if there is an evicted block, writeback; another memory request is generated
            if (Config.proc.wb == false) wb_addr = Proc.NULL_ADDRESS; 
            if (wb_addr != Proc.NULL_ADDRESS)
            {
                Req wb_req = RequestPool.depool();
                wb_req.set(pid, ReqType.WR, ReqType.NULL, wb_addr);
                bool wb_merge = wb_q.Exists(x => x.block_addr == wb_req.block_addr);
                if (!wb_merge) {
                    wb_q.Add(wb_req);
                }
                else {
                    RequestPool.enpool(wb_req);
                }
            }
 
            //destory req
            RequestPool.enpool(req);
            out_read_req--;
        }

        public void recv_wb_req(Req req)
        {
            //stats
            Stat.procs[pid].write_req_served.Collect();
            Stat.procs[pid].write_avg_latency.Collect(req.latency);

            //destroy req
            RequestPool.enpool(req);
        }

        public Req get_req()
        {
            Req wb_req = null;
            if (Config.pc_trace) 
            {
                trace.get_req(ref curr_cpu_inst_cnt, out curr_rd_req, out wb_req, ref pc);
                curr_rd_req.stall_time = criticality_table[(int)((pc >> 32) & 1023)];
                curr_rd_req.pc = pc;
            }
            else trace.get_req(ref curr_cpu_inst_cnt, out curr_rd_req, out wb_req);
            curr_rd_req.wb_req = wb_req;

            return curr_rd_req;
        }

        public bool issue_wb_req(Req wb_req)
        {
            if (Config.model_memory)
            {
                bool mctrl_ok = insert_mctrl(wb_req);
                return mctrl_ok;
            }
            else
            {
                add_to_mem_queue(curr_rd_req);
                return true; 
            }
        }

        public bool reissue_rd_req()
        {
            //retry mshr
            if (mshr_retry) {
                Dbg.Assert(!mctrl_retry);

                //retry mshr
                bool mshr_ok = insert_mshr(curr_rd_req);
                if (!mshr_ok) 
                    return false;
                
                //success
                mshr_retry = false;

                //check if true miss
                bool false_miss = inst_wnd.is_duplicate(curr_rd_req.block_addr);
                Dbg.Assert(!false_miss);

                //retry mctrl
                mctrl_retry = true;
            }

            //retry mctrl
            if (mctrl_retry) {
                Dbg.Assert(!mshr_retry);

                //retry mctrl
                bool mctrl_ok = insert_mctrl(curr_rd_req);
                if (!mctrl_ok) 
                    return false;
                
                //success
                mctrl_retry = false;
                return true;
            }

            //should never get here
            throw new System.Exception("Processor: Reissue Request");
        }

        public void add_to_cache_queue(Req req)
        {
            req.ts_departure = (long)(cycles + (ulong)Config.cache_hit_latency); 
            cache_hit_queue.AddLast(req);
            inst_wnd.add(req.block_addr, true, false, req.pc);
            return;
            
        }

        public void add_to_mem_queue(Req req)
        {
          
            req.ts_departure = (long)(cycles + (ulong)Config.mem_latency); 
            mem_queue.AddLast(req);
            return;
            
        }

        public void service_cache_queue()
        {
            while (cache_hit_queue.Count != 0)
            {
                Req first_request = cache_hit_queue.First.Value;
                if ((ulong)first_request.ts_departure <= cycles) 
                {
                    if (!l1_cache.has_addr(first_request.block_addr, ReqType.RD)) {
                        l1_cache.cache_add(first_request.block_addr, first_request.type, (ulong)pid);
                    }    
                   
                   
                    cache_hit_queue.RemoveFirst();
                    RequestPool.enpool(first_request); 
                    inst_wnd.set_ready(first_request.block_addr);
                }
                else return;
            }
        }


        public void service_mem_queue()
        {
            while (mem_queue.Count != 0)
            {
                Req first_request = mem_queue.First.Value;
                if ((ulong)first_request.ts_departure <= cycles) 
                {
                    Stat.procs[pid].read_req_served.Collect();
                    Stat.procs[pid].read_avg_latency.Collect(first_request.latency);
                    ulong wb_addr = Proc.NULL_ADDRESS;
 
                    if (!cache.has_addr(first_request.block_addr, first_request.proc_req_type))
                    {
                        wb_addr = cache.cache_add(first_request.block_addr, first_request.proc_req_type, (ulong)pid);
                        if (!l1_cache.has_addr(first_request.block_addr, first_request.type))
                        {
                            l1_cache.cache_add(first_request.block_addr, first_request.type, (ulong)pid);
                        }
                        l1_cache.cache_remove(wb_addr, ReqType.RD);
                    }
                    if (Config.proc.wb == false) wb_addr = Proc.NULL_ADDRESS; 
                    if (wb_addr != Proc.NULL_ADDRESS)
                    {
                        Req wb_req = RequestPool.depool();
                        wb_req.set(pid, ReqType.WR, ReqType.NULL, wb_addr);
                        bool wb_merge = wb_q.Exists(x => x.block_addr == wb_req.block_addr);
                        if (!wb_merge) {
                            wb_q.Add(wb_req);
                        }
                        else {
                            RequestPool.enpool(wb_req);
                        }
                    }
//
                    mem_queue.RemoveFirst();
                    RequestPool.enpool(first_request); 
                    inst_wnd.set_ready(first_request.block_addr);
                }
                else return;
            }
        }

        public void issue_insts(bool issued_rd_req)
        {
            //issue instructions
            for (int i = 0; i < Config.proc.ipc; i++) {
                if (inst_wnd.is_full()) {
                    if (i == 0) Stat.procs[pid].stall_inst_wnd.Collect();
                    return;
                }

//                Console.Write(" i - " + i + " Proc IPC - " + Config.proc.ipc + "\n");
                //cpu instructions
                if (curr_cpu_inst_cnt > 0) {
                    curr_cpu_inst_cnt--;
                    inst_wnd.add(0, false, true, 0);
                    continue;
                }

                //only one memory instruction can be issued per cycle
                if (issued_rd_req)
                    return;


                //check if true miss
                bool false_miss = inst_wnd.is_duplicate(curr_rd_req.block_addr);
                if (false_miss) {
                    Dbg.Assert(curr_rd_req.wb_req == null);
                    RequestPool.enpool(curr_rd_req);
                    curr_rd_req = get_req();
                    continue;
                }

                if (!Config.is_cache_filtered)
                {
                   bool is_in_l1_cache = false;
                   is_in_l1_cache = l1_cache.has_addr(curr_rd_req.block_addr, curr_rd_req.proc_req_type);
                   if (is_in_l1_cache)
                   {
                      Stat.procs[pid].l1_cache_hit_count.Collect();
                      RequestPool.enpool(curr_rd_req);
                      curr_rd_req = get_req();
                      inst_wnd.add(curr_rd_req.block_addr, true, true, curr_rd_req.pc);
                      continue;
                   }
                   Stat.procs[pid].l1_cache_miss_count.Collect();
                }



                is_in_cache = false;
                is_cache_hit = false;

                if (!Config.is_cache_filtered)
                {
                   is_in_cache = cache.has_addr(curr_rd_req.block_addr, curr_rd_req.proc_req_type);
                }

                //check if already in cache
                if (!Config.is_cache_filtered)
                {
                   if (is_in_cache)
                   {
                      Stat.procs[pid].l2_cache_hit_count.Collect();
                      add_to_cache_queue(curr_rd_req);
                      curr_rd_req = get_req();
                      continue;
                   }
                }

                inst_wnd.add(curr_rd_req.block_addr, true, false, false, curr_rd_req.pc);

                if (Config.model_memory)
                {

                    //try mshr
                    bool mshr_ok = insert_mshr(curr_rd_req);
                    if (!mshr_ok) {
                        mshr_retry = true;
                        return;
                    }
    
                    //try memory controller
                    bool mctrl_ok = insert_mctrl(curr_rd_req);
                    if (!mctrl_ok) {
                        mctrl_retry = true;
                        return;
                    }
                }
                else
                {
                    add_to_mem_queue(curr_rd_req);
                }
                Stat.procs[pid].l2_cache_miss_count.Collect();
                misses ++;

                //issued memory request
                issued_rd_req = true;

                //get new read request
                curr_rd_req = get_req();
            }

        }

        public void tick()
        {
            /*** Preamble ***/
            cycles++;
            
            Stat.procs[pid].cycle.Collect();
            inst_cnt = Stat.procs[pid].ipc.Count;

            if (cycles % 1000000 == 0)
            {
                Console.Write(" Processor " + pid + " Cycles " + cycles + " Instructions " + inst_cnt + "\n");	
            }
//            if ((inst_cnt/(ulong)Config.periodicDumpWindow) > (ulong)prev_dump)
            if (cycles % (ulong)Config.periodicDumpWindow  == 0)
            {
                prev_dump ++;
                Sim.periodic_writer_ipc.WriteLine(" Proc " + pid + " Cycles " + cycles + " Instructions " + inst_cnt  + " " + (double)(inst_cnt - prev_inst_cnt) / (double)(cycles - prev_cycle));
//               Sim.periodic_writer_ipc.WriteLine(" Proc " + pid + " Cycles " + cycles + " Instructions " + ((ulong)prev_dump * (ulong)Config.periodicDumpWindow)  + " " + (double)(inst_cnt - prev_inst_cnt) / (double)(cycles - prev_cycle));
                prev_inst_cnt = inst_cnt;
                prev_cycle = cycles;
                Sim.periodic_writer_ipc.Flush();

            }


 
            if (!Config.model_memory) service_mem_queue();
            service_cache_queue(); 
            if (inst_cnt != 0 && inst_cnt % 1000000 == 0) {
                ulong quantum = inst_cnt / 1000000;
                if (quantum > curr_quantum) {
                    curr_quantum = quantum;

                    ulong read_req = Stat.procs[pid].read_req.Count;
                    Stat.procs[pid].read_quantum.EndQuantum(read_req - prev_read_req);
                    prev_read_req = read_req;

                    ulong write_req = Stat.procs[pid].write_req.Count;
                    Stat.procs[pid].write_quantum.EndQuantum(write_req - prev_write_req);
                    prev_write_req = write_req;
                }
            }

            /*** Retire ***/
            int retired = inst_wnd.retire(Config.proc.ipc);
            Stat.procs[pid].ipc.Collect(retired);

            
            if (Config.pc_trace && inst_wnd.is_full()) 
            {
                just_set_full = true;
                full_address = (inst_wnd.pc_oldest() >> 32) & (ulong)1023;
                criticality_running_table[full_address] ++;
            }
            else
            {
                if (just_set_full) set_full = false;
                if (set_full == false)
                {
                    set_full = true;
                    just_set_full = false;
                    if (Config.sched.max_stall_crit)
                    {
                        if (criticality_running_table[full_address] > criticality_table[full_address]) 
                        {
                            criticality_table[full_address] = criticality_running_table[full_address];
                            criticality_running_table[full_address] = 0;
                        }
                    }
                    else
                    {
                        criticality_table[full_address] += criticality_running_table[full_address];
                        criticality_running_table[full_address] = 0;
                    }
                }
            }



            if (is_req_outstanding())
            {
                Stat.procs[pid].memory_cycle.Collect();
            }
            /*** Issue writeback request ***/
            if (Config.proc.wb && wb_q.Count > 0) {
                bool wb_ok = issue_wb_req(wb_q[0]);
                if (wb_ok) {
                    wb_q.RemoveAt(0);
                }

                //writeback stall
                bool stalled_wb = wb_q.Count > Config.proc.wb_q_max;
                if (stalled_wb)
                    return;
            }

            /*** Reissue previous read request ***/
            bool issued_rd_req = false;
            if (mshr_retry || mctrl_retry) {
                Dbg.Assert(curr_rd_req != null && curr_cpu_inst_cnt == 0);

                //mshr/mctrl stall
                bool reissue_ok = reissue_rd_req();
                if (!reissue_ok) 
                    return;

                //reissue success
                Dbg.Assert(!mshr_retry && !mctrl_retry);
                issued_rd_req = true;
                curr_rd_req = get_req();
            }

            /*** Issue instructions ***/
            Dbg.Assert(curr_rd_req != null);
            issue_insts(issued_rd_req);
        }

        private bool is_req_outstanding()
        {
            return (mshr.Count != 0);
        }

        private bool insert_mshr(Req req)
        {
            if (mshr.Count == mshr.Capacity) {
                return false;
            }
            mshr.Add(req.block_addr);
            return true;
        }

        private bool insert_mctrl(Req req)
        {
            MemAddr addr = req.addr;

            //failure
            if (Sim.mctrls[addr.cid].is_q_full(pid, req.type, addr.rid, addr.bid)) {
                return false;
            }
            //success
            send_req(req);
            return true;
        }

        private void send_req(Req req)
        {
            switch (req.type) {
                case ReqType.RD:
                    Stat.procs[pid].rmpc.Collect();
                    Stat.procs[pid].read_req.Collect();
                    req.callback = new Callback(recv_req);
                    out_read_req++;
                    break;
                case ReqType.WR:
                    Stat.procs[pid].wmpc.Collect();
                    Stat.procs[pid].write_req.Collect();
                    req.callback = new Callback(recv_wb_req);
                    break;
            }

            Stat.procs[pid].req.Collect();
            Sim.mctrls[req.addr.cid].enqueue_req(req);
        }

        public override string ToString()
        {
            return "Processor " + pid;
        }

        public ulong get_stall_shared_delta()
        {
            ulong temp = stall_shared_delta;
            stall_shared_delta = 0;
            return temp;
        }

        public ulong get_total_read_latency()
        {
            return total_read_latency;
        }

        public void reset_total_read_latency()
        {
            total_read_latency = 0;
        }


    }
}
